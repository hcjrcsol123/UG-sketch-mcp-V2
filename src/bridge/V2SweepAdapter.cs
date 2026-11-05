using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    internal sealed class F21SweepPlanData
    {
        internal M3SweepRequest Request;
        internal Curve[] Path;
        internal double[][] Points;
        internal string ProfileFingerprint;
        internal Dictionary<string, object> PriorDefinitions, SeedDefinitions, SketchDatumDefinitions, DefinitionAfter, GeometryAfter;
        internal Dictionary<string, object> ExpressionsBefore, ExpressionsAfter;
        internal Dictionary<string, Dictionary<string, object>> BodyDetailsBefore, BodyDetailsAfter;
        internal HashSet<string> OldMirrorBodyTags;
        internal Dictionary<string, List<string>> ChildrenBefore;
    }

    private const double F21Tol = 1.0e-6;
    private const double F21TangentTol = 1.0e-7;
    private static double[] F21Point(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static double F21Distance(double[] a, double[] b)
    { double x=a[0]-b[0],y=a[1]-b[1],z=a[2]-b[2]; return Math.Sqrt(x*x+y*y+z*z); }
    private static double F21Dot(double[] a,double[] b) { return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
    private static double[] F21Unit(double[] a)
    {
        double n=Math.Sqrt(F21Dot(a,a));
        if(!IsFinite(n) || n<=1.0e-12) throw new M2ModelException("UNSUPPORTED_SWEEP_PATH","path tangent is degenerate");
        return new double[]{a[0]/n,a[1]/n,a[2]/n};
    }
    private static bool F21InPart(Part part,NXObject item)
    { return item!=null && !item.IsOccurrence && item.OwningPart!=null && item.OwningPart.Tag.Equals(part.Tag); }

    private static void F21Ends(Curve curve,out double[][] ends,out double[][] tangents)
    {
        UFSession uf=UFSession.GetUFSession(); IntPtr ev=IntPtr.Zero;
        try
        {
            uf.Eval.Initialize2(curve.Tag,out ev);
            if(ev==IntPtr.Zero) throw new InvalidOperationException("path evaluator unavailable");
            double[] limits=new double[2]; uf.Eval.AskLimits(ev,limits);
            if(!IsFinite(limits[0]) || !IsFinite(limits[1]) || limits[0]>=limits[1]) throw new InvalidOperationException("invalid path domain");
            ends=new double[2][]; tangents=new double[2][];
            for(int i=0;i<2;i++)
            {
                double[] p=new double[3],d=new double[3]; uf.Eval.Evaluate(ev,1,limits[i],p,d);
                for(int j=0;j<3;j++) if(!IsFinite(p[j]) || !IsFinite(d[j])) throw new InvalidOperationException("nonfinite path geometry");
                ends[i]=p; tangents[i]=F21Unit(d);
            }
            if(F21Distance(ends[0],ends[1])<=F21Tol) throw new InvalidOperationException("closed or degenerate path element");
        }
        finally
        {
            if(ev!=IntPtr.Zero) try { uf.Eval.Free(ev); }
            catch(Exception ex) { throw M3AnalysisStateUnknown("sweep path evaluator cleanup failed",ex); }
        }
    }

    private static void F21Path(Part part,Sketch sketch,string[] ids,out Curve[] path,out double[][] points,out double[] firstTangent)
    {
        HashSet<string> sketchTags=new HashSet<string>(StringComparer.Ordinal);
        foreach(Sketch s in part.Sketches.ToArray())
        {
            if(s==null) throw new M2ModelException("MODEL_READ_FAILED","sketch inventory unavailable");
            NXObject[] geometry=s.GetAllGeometry(); if(geometry==null) throw new M2ModelException("MODEL_READ_FAILED","sketch geometry unavailable");
            foreach(NXObject obj in geometry) if(obj!=null) sketchTags.Add(obj.Tag.ToString());
        }
        Dictionary<string,Curve> available=new Dictionary<string,Curve>(StringComparer.Ordinal);
        foreach(Curve c in part.Curves.ToArray())
        {
            if(c==null || sketchTags.Contains(c.Tag.ToString())) continue;
            string id=RequiredJournalId(c,"path curve");
            if(available.ContainsKey(id)) throw new M2ModelException("UNSUPPORTED_SWEEP_PATH","ambiguous curve identifier");
            available.Add(id,c);
        }
        path=new Curve[ids.Length]; double[][][] ends=new double[ids.Length][][],tangents=new double[ids.Length][][];
        HashSet<string> seen=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<ids.Length;i++)
        {
            Curve c;
            if(String.IsNullOrWhiteSpace(ids[i]) || ids[i].Length>4096 || !seen.Add(ids[i]) || !available.TryGetValue(ids[i],out c) ||
                !F21InPart(part,c) || (!(c is Line) && !(c is Arc)))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PATH","path requires unique work-part non-sketch Line or Arc IDs");
            path[i]=c; F21Ends(c,out ends[i],out tangents[i]);
        }
        points=new double[ids.Length+1][]; points[0]=F21Point(sketch.Origin);
        firstTangent=null; double[] lastTangent=null;
        for(int i=0;i<path.Length;i++)
        {
            int match=-1,matchEnd=-1,count=0;
            for(int j=i;j<path.Length;j++) for(int k=0;k<2;k++)
                if(F21Distance(points[i],ends[j][k])<=F21Tol) {match=j;matchEnd=k;count++;}
            if(count!=1 || match!=i) throw new M2ModelException("UNSUPPORTED_SWEEP_PATH","ordered path must connect uniquely from sketch origin within 1e-6 mm");
            double sign=matchEnd==0?1.0:-1.0;
            double[] start=new double[]{sign*tangents[i][matchEnd][0],sign*tangents[i][matchEnd][1],sign*tangents[i][matchEnd][2]};
            double[] end=new double[]{sign*tangents[i][1-matchEnd][0],sign*tangents[i][1-matchEnd][1],sign*tangents[i][1-matchEnd][2]};
            if(lastTangent!=null && F21Distance(lastTangent,start)>F21TangentTol)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PATH","path elements must be tangent-continuous");
            if(firstTangent==null) firstTangent=start;
            lastTangent=end; points[i+1]=ends[i][1-matchEnd];
            for(int k=0;k<=i;k++) if(F21Distance(points[i+1],points[k])<=F21Tol)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PATH","closed or self-connected path is unsupported");
        }
    }

    private static int F21Vertex(List<double[]> vertices,double[] p)
    {
        int found=-1;
        for(int i=0;i<vertices.Count;i++) if(F21Distance(vertices[i],p)<=F21Tol)
        { if(found>=0) throw new M2ModelException("UNSUPPORTED_PROFILE","ambiguous profile vertex"); found=i; }
        if(found>=0)return found; vertices.Add(p); return vertices.Count-1;
    }

    private static void F21Profile(Part part,Sketch sketch,string output,double[] start,double[] tangent)
    {
        NXObject[] geometry=sketch.GetAllGeometry(); if(geometry==null) throw new M2ModelException("UNSUPPORTED_PROFILE","sketch geometry unavailable");
        List<double[]> vertices=new List<double[]>(); List<int[]> segments=new List<int[]>(); UFSession uf=UFSession.GetUFSession();
        foreach(NXObject item in geometry)
        {
            Curve curve=item as Curve; if(curve==null) throw new M2ModelException("UNSUPPORTED_PROFILE","sketch contains non-curve geometry");
            UFSket.ReferenceStatus status; uf.Sket.AskReferenceStatus(sketch.Tag,curve.Tag,out status);
            if(status==UFSket.ReferenceStatus.Reference)continue;
            Line line=curve as Line;
            if(status!=UFSket.ReferenceStatus.Active || line==null || !F21InPart(part,line))
                throw new M2ModelException("UNSUPPORTED_PROFILE","only active same-part Line profile geometry is supported");
            int a=F21Vertex(vertices,F21Point(line.StartPoint)),b=F21Vertex(vertices,F21Point(line.EndPoint));
            if(a==b) throw new M2ModelException("UNSUPPORTED_PROFILE","degenerate profile line");
            foreach(int[] s in segments) if((s[0]==a && s[1]==b)||(s[0]==b && s[1]==a))
                throw new M2ModelException("UNSUPPORTED_PROFILE","duplicate profile line");
            segments.Add(new int[]{a,b});
        }
        int n=vertices.Count,m=segments.Count;
        if(m==0 || n>256 || (output=="solid" && (n<3 || m!=n)) || (output=="sheet" && m!=n-1 && !(n>=3 && m==n)))
            throw new M2ModelException("UNSUPPORTED_PROFILE","solid requires one closed polygon; sheet requires one open or closed line chain");
        int[] degree=new int[n]; foreach(int[] s in segments){degree[s[0]]++;degree[s[1]]++;}
        int endpoints=0; foreach(int d in degree) {if(d==1)endpoints++;else if(d!=2)throw new M2ModelException("UNSUPPORTED_PROFILE","profile branches");}
        if(endpoints!=(m==n?0:2))throw new M2ModelException("UNSUPPORTED_PROFILE","profile connectivity is invalid");
        bool[] reached=new bool[n];reached[0]=true;
        for(int pass=0;pass<n;pass++)foreach(int[] s in segments)if(reached[s[0]]||reached[s[1]]){reached[s[0]]=true;reached[s[1]]=true;}
        int anchors=0; foreach(bool ok in reached)if(!ok)throw new M2ModelException("UNSUPPORTED_PROFILE","profile must be connected");
        foreach(double[] v in vertices)
        {
            if(F21Distance(v,start)<=F21Tol)anchors++;
            double[] offset=new double[]{v[0]-start[0],v[1]-start[1],v[2]-start[2]};
            if(Math.Abs(F21Dot(offset,tangent))>F21Tol)throw new M2ModelException("UNSUPPORTED_PROFILE","profile plane must be normal to the initial path tangent");
        }
        if(anchors!=1)throw new M2ModelException("UNSUPPORTED_PROFILE","path origin must match one profile vertex");
    }

    private static Dictionary<string,object> F21CaptureSeedDefinitions(Part part)
    {
        Dictionary<string,object> records=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(Feature f in part.Features)
        {
            if(!(f is Block) && !(f is Extrude)) continue;
            string id=RequiredJournalId(f,"sweep prior feature");
            try { records.Add(id,V2FeaturePatternFactory.ReadSeedIsolated(Session.GetSession(),part,f,delegate{return F19Inventory(part);})); }
            catch(V2FeaturePatternFactory.ReadStateUnknownException ex) { throw M3AnalysisStateUnknown("sweep prior feature reader could not restore state",ex); }
            catch(Exception ex) { throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","prior block or extrude definition is unavailable ("+ex.GetType().Name+")"); }
        }
        return records;
    }

    // No undo or update: an unexpected state change makes the read outcome unknown.
    private static T F21AuditRead<T>(Part part, Func<T> read)
    {
        bool dirty; string before;
        try
        {
            dirty=part.IsModified; before=F19Inventory(part);
            if(part.IsModified!=dirty)throw new InvalidOperationException("initial inventory changed dirty state");
        }
        catch(Exception ex){throw M3AnalysisStateUnknown("sweep datum initial state could not be established",ex);}
        T result=default(T); Exception failure=null;
        try {result=read();} catch(Exception ex){failure=ex;}
        try
        {
            if(part.IsModified!=dirty)throw new InvalidOperationException("datum read changed dirty state");
            string after=F19Inventory(part);
            if(part.IsModified!=dirty || !String.Equals(before,after,StringComparison.Ordinal))
                throw new InvalidOperationException("datum read changed captured inventory");
        }
        catch(Exception ex){throw M3AnalysisStateUnknown("sweep datum read state could not be proven unchanged",ex);}
        if(failure!=null)throw failure;
        return result;
    }

    private static NXObject F21DatumObject(Part part,Tag tag,string label)
    {
        if(tag==Tag.Null)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION",label+" tag is null");
        NXObject item=NXOpen.Utilities.NXObjectManager.Get(tag) as NXObject;
        if(!F21InPart(part,item) || !item.Tag.Equals(tag))
            throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION",label+" is not a unique native work-part object");
        return item;
    }

    private static void F21DatumVector(double[] value,string label)
    {
        if(value==null || value.Length!=3 || !IsFinite(value[0]) || !IsFinite(value[1]) || !IsFinite(value[2]))
            throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION",label+" is not a finite vector");
    }

    private static double[] F21Vector(Vector3d v){return new double[]{v.X,v.Y,v.Z};}
    private static double[] F21Cross(double[] a,double[] b)
    {return new double[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};}

    // UF_SO_ASK_ALL_PARENTS, UF_SO_ASK_UNLOADED_PARENTS and
    // UF_SO_ASK_DUMB_SO_PARENTS are defined in the installed uf_so.h.
    private static HashSet<string> F21DatumParents(Part part,Tag tag,HashSet<string> allowed,bool recursive)
    {
        HashSet<string> result=new HashSet<string>(StringComparer.Ordinal);
        foreach(NXObject item in F21QueryDatumParents(part,tag,recursive))
        {
            string key=item.Tag.ToString();
            if(!allowed.Contains(key) || !result.Add(key))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum has an unexpected or duplicate parent");
        }
        return result;
    }

    private static List<NXObject> F21QueryDatumParents(Part part,Tag tag,bool recursive)
    {
        UFSession uf=UFSession.GetUFSession();bool isSo,isSubclass;
        uf.So.IsSo(tag,out isSo);uf.So.IsSubclass(tag,out isSubclass);
        if(!isSo || !isSubclass)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","parent query requires a parameterized smart object");
        int status;uf.So.AskParentStatus(tag,out status);
        if(status!=0)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum smart-object parent status is not clean");
        int count;Tag[] parents;int options=8|32|512|(recursive?16:0);
        uf.So.AskParents(tag,options,out count,out parents);
        if(count<0 || (parents==null ? count!=0 : parents.Length!=count))
            throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum parent list is unavailable");
        List<NXObject> result=new List<NXObject>();HashSet<string> seen=new HashSet<string>(StringComparer.Ordinal);
        if(parents!=null)foreach(Tag parent in parents)
        {
            NXObject item=F21DatumObject(part,parent,"datum parent"); string key=item.Tag.ToString();
            if(key==tag.ToString() || !seen.Add(key))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum has unknown, cyclic or duplicate parents");
            result.Add(item);
        }
        return result;
    }

    private static string F21EnumText(object value,string label)
    {
        if(value==null || !value.GetType().IsEnum || !Enum.IsDefined(value.GetType(),value))
            throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION",label+" is not a recognized enum value");
        return value.ToString();
    }

    private static Dictionary<string,object> F21DatumLeaf(Part part,NXObject item)
    {
        UFSession uf=UFSession.GetUFSession();bool isSo,isSubclass;
        uf.So.IsSo(item.Tag,out isSo);uf.So.IsSubclass(item.Tag,out isSubclass);
        if(!isSubclass)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum source is not a smart-object subclass");
        int nativeType,nativeSubtype;uf.Obj.AskTypeAndSubtype(item.Tag,out nativeType,out nativeSubtype);
        Direction direction=item as Direction;
        if(direction!=null)
        {
            if(isSo)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum source direction must be non-parameterized");
            double[] origin=F21Point(direction.Origin),vector=F21Vector(direction.Vector);
            F21DatumVector(origin,"source direction origin");F21DatumVector(vector,"source direction vector");
            string sense=F21EnumText(direction.Sense,"direction sense");
            if(Math.Abs(F21Dot(vector,vector)-1)>1e-9 || sense!="Forward")
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","source direction is not unit length");
            return new Dictionary<string,object>{{"kind","Direction"},{"tag",item.Tag.ToString()},
                {"is_so",isSo},{"is_subclass",isSubclass},{"native_type",nativeType},{"native_subtype",nativeSubtype},
                {"origin",origin},{"vector",vector},{"sense",sense},
                {"update",F21EnumText(direction.Update,"direction update")}};
        }
        Scalar scalar=item as Scalar;
        if(scalar!=null)
        {
            if(isSo)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum source scalar must be non-parameterized");
            double nativeValue;uf.So.AskDoubleOfScalar(item.Tag,out nativeValue);
            string dimension=F21EnumText(scalar.Dimensionality,"scalar dimensionality");
            if(!IsFinite(scalar.Value) || !IsFinite(nativeValue) || dimension!="None" ||
               Math.Abs(nativeValue-1)>1e-9 || Math.Abs(nativeValue-scalar.Value)>1e-9 || !String.IsNullOrEmpty(scalar.MeasurementName))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","source scalar must be an unmeasured unit constant");
            return new Dictionary<string,object>{{"kind","Scalar"},{"tag",item.Tag.ToString()},
                {"is_so",isSo},{"is_subclass",isSubclass},{"native_type",nativeType},{"native_subtype",nativeSubtype},
                {"dimensionality",dimension},{"value",scalar.Value},{"uf_double",nativeValue},{"measurement_name",scalar.MeasurementName},
                {"update",F21EnumText(scalar.Update,"scalar update")}};
        }
        Plane plane=item as Plane;
        if(plane!=null)
        {
            if(!isSo || F21QueryDatumParents(part,item.Tag,false).Count!=0 || F21QueryDatumParents(part,item.Tag,true).Count!=0)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","source plane has an unsupported parent graph");
            NXObject[] geometry=plane.GetGeometry();
            if(plane.Method!=PlaneTypes.MethodType.Undefined || geometry==null || geometry.Length!=0 ||
               plane.Expression!=null || plane.OffsetExpression!=null || plane.Flip || plane.OffsetFlip)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","source plane is not an independent fixed plane");
            double[] origin=F21Point(plane.Origin),normal=F21Vector(plane.Normal);
            F21DatumVector(origin,"source plane origin");F21DatumVector(normal,"source plane normal");
            if(Math.Abs(F21Dot(normal,normal)-1)>1e-9)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","source plane normal is not unit length");
            return new Dictionary<string,object>{{"kind","Plane"},{"tag",item.Tag.ToString()},
                {"is_so",isSo},{"is_subclass",isSubclass},{"native_type",nativeType},{"native_subtype",nativeSubtype},
                {"method",F21EnumText(plane.Method,"plane method")},{"geometry",new object[0]},
                {"origin",origin},{"normal",normal},{"expression",null},{"offset_expression",null},{"flip",plane.Flip},{"offset_flip",plane.OffsetFlip},
                {"update",F21EnumText(plane.Update,"plane update")}};
        }
        NXOpen.Point point=item as NXOpen.Point;
        if(point!=null)
        {
            if(isSo || point.IsReference)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum source point must be a non-reference constant");
            double[] coordinates=F21Point(point.Coordinates);F21DatumVector(coordinates,"source point");
            return new Dictionary<string,object>{{"kind","Point"},{"tag",item.Tag.ToString()},
                {"is_so",isSo},{"is_subclass",isSubclass},{"native_type",nativeType},{"native_subtype",nativeSubtype},
                {"coordinates",coordinates},{"is_reference",point.IsReference},{"update",F21EnumText(point.Update,"point update")}};
        }
        throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum source type is not supported");
    }

    private static void F21DatumVisit(string node,Dictionary<string,HashSet<string>> edges,HashSet<string> visiting,HashSet<string> done)
    {
        if(done.Contains(node))return;
        if(!visiting.Add(node))throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum dependency graph contains a cycle");
        foreach(string parent in edges[node])F21DatumVisit(parent,edges,visiting,done);
        visiting.Remove(node);done.Add(node);
    }

    private static Dictionary<string,object> F21ReadSketchDatum(Part part,DatumCsys datum,Sketch sketch)
    {
        return F21AuditRead(part,delegate {return F21ReadSketchDatumCore(part,datum,sketch);});
    }

    // Callers of the core must supply an equivalent before/after state audit.
    private static Dictionary<string,object> F21ReadSketchDatumCore(Part part,DatumCsys datum,Sketch sketch)
    {
        Plane ignored;return F21ReadSketchDatumCore(part,datum,sketch,out ignored);
    }

    private static Dictionary<string,object> F21ReadSketchDatumCore(Part part,DatumCsys datum,Sketch sketch,out Plane sourcePlane)
    {
            sourcePlane=null;
            if(!F21InPart(part,datum) || !F21InPart(part,sketch) || sketch.Feature==null || !F21InPart(part,sketch.Feature))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum or sketch is not native to the work part");
            Feature[] parents=datum.GetParents(),children=datum.GetChildren(),sketchParents=sketch.Feature.GetParents();
            if(parents==null || parents.Length!=0 || children==null || children.Length!=1 || children[0]==null ||
               !children[0].Tag.Equals(sketch.Feature.Tag) || sketchParents==null || sketchParents.Length!=1 ||
               sketchParents[0]==null || !sketchParents[0].Tag.Equals(datum.Tag))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum is not the unique parent of one sketch feature");
            Expression[] expressions=datum.GetExpressions(); NXObject[] entities=datum.GetEntities();
            if(expressions==null || expressions.Length!=0 || entities==null)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum expressions or entities are not a fixed closed definition");
            // The observed sketch-owned internal datum reports associative even
            // when its complete, same-part smart-object graph is closed.
            bool associative=datum.IsAssociative();
            if(!associative)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","unsupported sketch datum association mode");
            UFSession uf=UFSession.GetUFSession();Tag csysTag,originTag;Tag[] axisTags,planeTags;
            uf.Modl.AskDatumCsysComponents(datum.Tag,out csysTag,out originTag,out axisTags,out planeTags);
            if(axisTags==null || axisTags.Length!=3 || planeTags==null || planeTags.Length!=3)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum component list is incomplete");
            Tag xformTag=uf.Modl.AskXformTagOfDatumCsys(datum.Tag);
            List<Tag> componentTags=new List<Tag>();componentTags.Add(originTag);componentTags.AddRange(axisTags);componentTags.AddRange(planeTags);
            List<Tag> allTags=new List<Tag>();allTags.Add(csysTag);allTags.Add(xformTag);allTags.AddRange(componentTags);
            HashSet<string> tags=new HashSet<string>(StringComparer.Ordinal);
            foreach(Tag tag in allTags){NXObject item=F21DatumObject(part,tag,"datum component");if(!tags.Add(item.Tag.ToString()))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum components do not have unique identities");}
            foreach(Tag tag in componentTags)
            {
                bool isSo;uf.So.IsSo(tag,out isSo);
                if(isSo)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum component must be non-parameterized");
            }
            HashSet<string> expectedEntities=new HashSet<string>(StringComparer.Ordinal);
            foreach(Tag tag in componentTags)expectedEntities.Add(tag.ToString());
            expectedEntities.Add(csysTag.ToString());
            HashSet<string> actualEntities=new HashSet<string>(StringComparer.Ordinal);
            foreach(NXObject entity in entities)
            {
                if(!F21InPart(part,entity) || !actualEntities.Add(entity.Tag.ToString()))
                    throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum entity is ambiguous or external");
            }
            if(!actualEntities.SetEquals(expectedEntities))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum entity set is not the supported component set");
            // Origin, datum axes and datum planes are not smart objects. Query
            // only the CSYS, Xform and the Xform's four smart source objects.
            List<NXObject> sources=F21QueryDatumParents(part,xformTag,false);
            if(sources.Count!=4)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum transform must have exactly four fixed sources");
            HashSet<string> sourceTags=new HashSet<string>(StringComparer.Ordinal),kinds=new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string,object> sourceDefinitions=new Dictionary<string,object>(StringComparer.Ordinal);
            Direction sourceDirection=null;NXOpen.Point sourcePoint=null;
            foreach(NXObject source in sources)
            {
                string sourceTag=source.Tag.ToString();
                if(tags.Contains(sourceTag) || !sourceTags.Add(sourceTag))
                    throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum source identity is ambiguous");
                Dictionary<string,object> sourceDefinition=F21DatumLeaf(part,source);
                if(!kinds.Add((string)sourceDefinition["kind"]))
                    throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum source types are not unique");
                if(sourceDefinition["kind"].Equals("Plane"))sourcePlane=source as Plane;
                if(sourceDefinition["kind"].Equals("Direction"))sourceDirection=source as Direction;
                if(sourceDefinition["kind"].Equals("Point"))sourcePoint=source as NXOpen.Point;
                sourceDefinitions.Add(sourceTag,sourceDefinition);
            }
            if(sourcePlane==null || sourceDirection==null || sourcePoint==null || !kinds.SetEquals(new string[]{"Direction","Scalar","Plane","Point"}))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum transform source types are not supported");
            HashSet<string> csysDirect=new HashSet<string>(new string[]{xformTag.ToString()},StringComparer.Ordinal);
            if(!F21DatumParents(part,csysTag,csysDirect,false).SetEquals(csysDirect) ||
               !F21DatumParents(part,xformTag,sourceTags,true).SetEquals(sourceTags))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum transform parent graph differs from its fixed sources");
            HashSet<string> csysRecursive=new HashSet<string>(sourceTags,StringComparer.Ordinal);csysRecursive.Add(xformTag.ToString());
            if(!F21DatumParents(part,csysTag,csysRecursive,true).SetEquals(csysRecursive))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum coordinate system parent closure is incomplete");
            NXObject attached=sketch.AttachPlane as NXObject;
            if(!F21InPart(part,attached) || !attached.Tag.Equals(planeTags[0]))
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","sketch is not attached to this datum's XY plane");
            double[] origin=new double[3],matrix=new double[16],x=new double[3],y=new double[3],z=new double[3];double scale;
            uf.So.AskPointOfXform(xformTag,origin);uf.So.AskMatrixOfXform(xformTag,matrix);uf.So.AskScaleOfXform(xformTag,out scale);
            uf.So.AskXDirectionOfXform(xformTag,x);uf.So.AskYDirectionOfXform(xformTag,y);uf.So.AskZDirectionOfXform(xformTag,z);
            F21DatumVector(origin,"datum origin");F21DatumVector(x,"datum x");F21DatumVector(y,"datum y");F21DatumVector(z,"datum z");
            foreach(double value in matrix)if(!IsFinite(value))throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum matrix is not finite");
            double[] matrixRotation=new double[9],matrixTranslation=new double[3];double matrixScale;
            uf.Mtx4.AskRotation(matrix,matrixRotation);uf.Mtx4.AskTranslation(matrix,matrixTranslation);uf.Mtx4.AskScale(matrix,out matrixScale);
            F21DatumVector(matrixTranslation,"datum matrix translation");
            foreach(double value in matrixRotation)if(!IsFinite(value))throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum matrix rotation is not finite");
            if(!IsFinite(scale) || Math.Abs(scale-1)>1e-9 || Math.Abs(F21Dot(x,x)-1)>1e-9 || Math.Abs(F21Dot(y,y)-1)>1e-9 ||
               Math.Abs(F21Dot(z,z)-1)>1e-9 || Math.Abs(F21Dot(x,y))>1e-9 || Math.Abs(F21Dot(x,z))>1e-9 ||
               Math.Abs(F21Dot(y,z))>1e-9 || F21Distance(F21Cross(x,y),z)>1e-9 || !IsFinite(matrixScale) ||
               Math.Abs(matrixScale-scale)>1e-9 || F21Distance(matrixTranslation,origin)>F21Tol)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum frame is not unit scale and right-handed orthonormal");
            NXOpen.CoordinateSystem csys=F21DatumObject(part,csysTag,"datum coordinate system") as NXOpen.CoordinateSystem;
            NXOpen.Point point=F21DatumObject(part,originTag,"datum origin point") as NXOpen.Point;
            if(csys==null || point==null)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum component types are unsupported");
            Matrix3x3 c=csys.Orientation.Element,s=sketch.Orientation.Element;
            double[][] frame=new double[][]{x,y,z};
            double[][] csysFrame=new double[][]{new double[]{c.Xx,c.Xy,c.Xz},new double[]{c.Yx,c.Yy,c.Yz},new double[]{c.Zx,c.Zy,c.Zz}};
            double[][] sketchFrame=new double[][]{new double[]{s.Xx,s.Xy,s.Xz},new double[]{s.Yx,s.Yy,s.Yz},new double[]{s.Zx,s.Zy,s.Zz}};
            double[] csysOrigin=F21Point(csys.Origin),pointOrigin=F21Point(point.Coordinates),sketchOrigin=F21Point(sketch.Origin);
            F21DatumVector(csysOrigin,"coordinate system origin");F21DatumVector(pointOrigin,"point origin");F21DatumVector(sketchOrigin,"sketch origin");
            double[] sourcePlaneOrigin=F21Point(sourcePlane.Origin),sourcePlaneNormal=F21Vector(sourcePlane.Normal);
            double[] sourceDirectionOrigin=F21Point(sourceDirection.Origin),sourceDirectionVector=F21Vector(sourceDirection.Vector),sourcePointOrigin=F21Point(sourcePoint.Coordinates);
            F21DatumVector(sourcePlaneOrigin,"source plane origin");F21DatumVector(sourcePlaneNormal,"source plane normal");
            F21DatumVector(sourceDirectionOrigin,"source direction origin");F21DatumVector(sourceDirectionVector,"source direction vector");F21DatumVector(sourcePointOrigin,"source point origin");
            if(F21Distance(sourcePlaneOrigin,origin)>F21Tol || F21Distance(sourcePlaneNormal,z)>1e-9 ||
               F21Distance(sourceDirectionOrigin,origin)>F21Tol || F21Distance(sourceDirectionVector,x)>1e-9 || F21Distance(sourcePointOrigin,origin)>F21Tol)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum sources do not match the native coordinate frame");
            if(F21Distance(origin,csysOrigin)>F21Tol || F21Distance(origin,pointOrigin)>F21Tol || F21Distance(origin,sketchOrigin)>F21Tol)
                throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum origin differs from components or sketch");
            for(int i=0;i<3;i++)
            {
                F21DatumVector(csysFrame[i],"coordinate system direction");F21DatumVector(sketchFrame[i],"sketch direction");
                double[] rotationRow=new double[]{matrixRotation[3*i],matrixRotation[3*i+1],matrixRotation[3*i+2]};
                if(F21Distance(frame[i],csysFrame[i])>1e-9 || F21Distance(frame[i],sketchFrame[i])>1e-9 || F21Distance(frame[i],rotationRow)>1e-9)
                    throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum frame differs from matrix, coordinate system or sketch");
            }
            List<object> components=new List<object>();
            for(int i=0;i<3;i++)
            {
                DatumAxis axis=F21DatumObject(part,axisTags[i],"datum axis") as DatumAxis;
                DatumPlane plane=F21DatumObject(part,planeTags[i],"datum plane") as DatumPlane;
                if(axis==null || plane==null)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum component type is unsupported");
                double[] ao=F21Point(axis.Origin),ad=F21Vector(axis.Direction),po=F21Point(plane.Origin),pn=F21Vector(plane.Normal);
                F21DatumVector(ao,"axis origin");F21DatumVector(ad,"axis direction");F21DatumVector(po,"plane origin");F21DatumVector(pn,"plane normal");
                if(F21Distance(ao,origin)>F21Tol || F21Distance(po,origin)>F21Tol ||
                   F21Distance(ad,frame[i])>1e-9 || F21Distance(pn,frame[(i+2)%3])>1e-9)
                    throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum axis or plane differs from frame");
                components.Add(new object[]{axis.Tag.ToString(),ao,ad,plane.Tag.ToString(),po,pn});
            }
            return new Dictionary<string,object>{{"feature_id",RequiredJournalId(datum,"sketch datum")},{"feature_tag",datum.Tag.ToString()},
                {"sketch_id",RequiredJournalId(sketch,"sketch")},{"sketch_feature_tag",sketch.Feature.Tag.ToString()},
                {"associative",associative},
                {"csys_tag",csysTag.ToString()},{"xform_tag",xformTag.ToString()},{"origin_tag",originTag.ToString()},
                {"origin",origin},{"x",x},{"y",y},{"z",z},{"matrix",matrix},{"scale",scale},{"components",components},
                {"sources",sourceDefinitions},{"source_plane_tag",sourcePlane.Tag.ToString()},{"attach_plane_tag",attached.Tag.ToString()}};
    }

    private static Dictionary<string,object> F21CaptureSketchDatums(Part part)
    {
        return F21AuditRead(part,delegate
        {
            Dictionary<string,Sketch> sketches=new Dictionary<string,Sketch>(StringComparer.Ordinal);
            foreach(Sketch sketch in part.Sketches.ToArray())
            {
                if(!F21InPart(part,sketch) || sketch.Feature==null || !F21InPart(part,sketch.Feature) ||
                   sketches.ContainsKey(sketch.Feature.Tag.ToString()))
                    throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","sketch feature mapping is not unique and native");
                sketches.Add(sketch.Feature.Tag.ToString(),sketch);
            }
            Dictionary<string,object> result=new Dictionary<string,object>(StringComparer.Ordinal);
            HashSet<string> usedSketches=new HashSet<string>(StringComparer.Ordinal);
            foreach(Feature feature in part.Features)
            {
                DatumCsys datum=feature as DatumCsys;if(datum==null)continue;
                Feature[] children=datum.GetChildren();Sketch sketch;
                if(children==null || children.Length!=1 || children[0]==null || !sketches.TryGetValue(children[0].Tag.ToString(),out sketch))continue;
                if(!usedSketches.Add(children[0].Tag.ToString()))
                    throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","multiple datum features claim one sketch");
                string id=RequiredJournalId(datum,"sketch datum");
                if(result.ContainsKey(id))throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","datum feature identity is ambiguous");
                result.Add(id,F21ReadSketchDatum(part,datum,sketch));
            }
            return result;
        });
    }

    private static void F21RequireSupportedPriorFeatures(Part part,M3PlanState plan)
    {
        HashSet<string> sketchTags=new HashSet<string>(StringComparer.Ordinal);
        foreach(Sketch s in part.Sketches.ToArray())
        {
            if(s==null || s.Feature==null)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","sketch feature identity unavailable");
            sketchTags.Add(s.Feature.Tag.ToString());
        }
        foreach(Feature f in part.Features)
        {
            if(f==null)throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","null feature");
            string id=RequiredJournalId(f,"prior feature");
            if(sketchTags.Contains(f.Tag.ToString()) || f is DatumPlaneFeature || plan.F21Sweep.SketchDatumDefinitions.ContainsKey(id) || plan.F21Sweep.SeedDefinitions.ContainsKey(id) ||
               plan.HoleDefinitionsBefore.ContainsKey(id) || plan.BooleanDefinitionsBefore.ContainsKey(id) ||
               plan.EdgeBlendDefinitionsBefore.ContainsKey(id) || plan.ChamferDefinitionsBefore.ContainsKey(id) ||
               plan.F18PatternsBefore.ContainsKey(id) || plan.F19MirrorsBefore.ContainsKey(id) || plan.F21Sweep.PriorDefinitions.ContainsKey(id)) continue;
            throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","prior feature has no supported native definition reader: "+id);
        }
    }

    private static Dictionary<string,List<string>> F21Children(Part part)
    {
        Dictionary<string,List<string>> result=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        foreach(Feature f in part.Features)
        {
            Feature[] children=f.GetChildren(); if(children==null)throw new InvalidOperationException("feature children unavailable");
            List<string> tags=new List<string>();foreach(Feature child in children){if(child==null)throw new InvalidOperationException("null child");tags.Add(child.Tag.ToString());}
            tags.Sort(StringComparer.Ordinal);result.Add(f.Tag.ToString(),tags);
        }
        return result;
    }

    public M3PlanSnapshot PrepareSweep(M2ModelBinding binding,M3SweepRequest source)
    {
        if(source==null || String.IsNullOrWhiteSpace(source.RevisionId) || String.IsNullOrWhiteSpace(source.SketchId) ||
           source.PathCurveIds==null || source.PathCurveIds.Length<1 || source.PathCurveIds.Length>32 ||
           (source.ProfileOutput!="solid" && source.ProfileOutput!="sheet") ||
           (source.OrientationMode!="transport" && source.OrientationMode!="linear_twist") || !IsFinite(source.TwistAngleDeg) ||
           Math.Abs(source.TwistAngleDeg)>90 || (source.OrientationMode=="transport" && source.TwistAngleDeg!=0))
            throw new M2ModelException("INVALID_REQUEST","invalid sweep request");
        BindingState state=RequireState(binding); Part part=state.WorkPart; bool dirty=part.IsModified; string inventory;
        try {inventory=F19Inventory(part);if(part.IsModified!=dirty)throw new InvalidOperationException("dirty state changed");}
        catch(Exception ex){throw M3AnalysisStateUnknown("sweep initial inventory changed model state",ex);}
        RequireWritableSimplePart(part,true);RequireMillimeterPartAndUnit(part,"mm","mm");
        if(state.Session.ActiveSketch!=null)throw new M2ModelException("NX_BUSY","end active sketch");
        SnapshotResult capture=Capture(binding,state);
        if(!String.Equals(source.RevisionId,binding.RevisionId,StringComparison.Ordinal))throw new M2ModelException("STALE_REVISION","revision changed");
        object sweptValue;Dictionary<string,object> sweptSnapshot;
        if(!capture.Snapshot.TryGetValue("native_swept_definitions",out sweptValue) ||
           (sweptSnapshot=sweptValue as Dictionary<string,object>)==null ||
           !sweptSnapshot.ContainsKey("status") || !Object.Equals(sweptSnapshot["status"],"SUPPORTED"))
            throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","existing Swept definitions cannot be proven within the supported LINE projection");
        Sketch sketch=FindSketch(part,source.SketchId);
        if(!F21InPart(part,sketch) || sketch.Feature==null)throw new M2ModelException("UNSUPPORTED_PROFILE","native work-part sketch is required");
        Curve[] path;double[][] points;double[] tangent;F21Path(part,sketch,source.PathCurveIds,out path,out points,out tangent);
        F21Profile(part,sketch,source.ProfileOutput,points[0],tangent);
        Dictionary<string,object> analysis;
        try{analysis=V2SketchProfileAnalyzer.AnalyzeF12WholeSketchSection(part,sketch);}
        catch(V2SketchProfileAnalyzer.SectionCleanupException ex){throw M3AnalysisStateUnknown("sweep profile analysis cleanup failed",ex);}
        if(!F12SectionAnalysisVerified(analysis))throw new M2ModelException("UNSUPPORTED_PROFILE","whole-sketch profile analysis did not verify");
        int opens=ProfileCount(analysis,"open_component_count"),closed=ProfileCount(analysis,"valid_closed_component_count");
        if((source.ProfileOutput=="solid" && (opens!=0 || closed!=1)) || (source.ProfileOutput=="sheet" && opens+closed!=1))
            throw new M2ModelException("UNSUPPORTED_PROFILE","exactly one supported profile component is required");
        string fingerprint;
        if(!F13TryProfileFingerprint(analysis,"input_geometry_domain_fingerprint",out fingerprint))throw new M2ModelException("UNSUPPORTED_PROFILE","profile domain fingerprint unavailable");
        M3PlanState plan=CreateM3PlanState(binding,state,"create_sweep",capture,F12MassPropertyAccuracy);
        plan.F21Sweep=new F21SweepPlanData(); F21SweepPlanData data=plan.F21Sweep;
        data.Request=new M3SweepRequest {RevisionId=source.RevisionId,SketchId=source.SketchId,PathCurveIds=(string[])source.PathCurveIds.Clone(),
            ProfileOutput=source.ProfileOutput,OrientationMode=source.OrientationMode,TwistAngleDeg=source.TwistAngleDeg};
        data.Path=path;data.Points=points;data.ProfileFingerprint=fingerprint;
        plan.Sketch=sketch;plan.SketchId=RequiredJournalId(sketch,"sweep sketch");plan.ProfileAnalysis=analysis;
        plan.SketchRecordBefore=Clone(FindSketchRecord(capture.Snapshot,plan.SketchId));plan.F12SemanticBefore=capture.Snapshot;
        plan.F12CurveInventoryBefore=M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(part));
        plan.HoleHelpersBefore=F14CaptureHelpers(part);plan.HoleDefinitionsBefore=F14CapturePersistentHoleDefinitions(part,null);
        plan.BooleanDefinitionsBefore=F15CaptureDefinitions(part,null);plan.EdgeBlendDefinitionsBefore=F16CaptureDefinitions(part,null);
        plan.ChamferDefinitionsBefore=F17CaptureDefinitions(part,null);plan.F18PatternReferenceTagsBefore=new Dictionary<string,string>(StringComparer.Ordinal);
        plan.F18PatternsBefore=F18CaptureDefinitions(part,null,plan.F18PatternReferenceTagsBefore);plan.F19MirrorsBefore=F19CaptureDefinitions(part,null);
        data.PriorDefinitions=F21CaptureDefinitions(part,null);data.SeedDefinitions=F21CaptureSeedDefinitions(part);
        data.SketchDatumDefinitions=F21CaptureSketchDatums(part);F21RequireSupportedPriorFeatures(part,plan);
        plan.F19PlanesBefore=F19PlaneInventory(part);plan.F19DatumsBefore=F19DatumInventory(part);
        data.ExpressionsBefore=F19ExpressionStates(part);data.ChildrenBefore=F21Children(part);
        plan.F18StableBefore=F18StableInventory(part,null);plan.F18InventoryBefore=inventory;
        data.OldMirrorBodyTags=new HashSet<string>(StringComparer.Ordinal);
        foreach(object value in plan.F19MirrorsBefore.Values)
        {
            Dictionary<string,object> row=value as Dictionary<string,object>;Body body=row==null?null:FindBody(part,row["body_id"] as string);
            if(body==null || !data.OldMirrorBodyTags.Add(body.Tag.ToString()))throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION","old mirror body cannot be identified");
        }
        data.BodyDetailsBefore=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
        plan.BodyFingerprintsBefore=CaptureF12BodyFingerprints(part,plan.BodiesBefore,data.BodyDetailsBefore);
        if(part.IsModified!=dirty || plan.WorkPartModifiedBefore!=dirty || inventory!=F19Inventory(part))
            throw M3AnalysisStateUnknown("sweep preparation changed model state",null);
        Dictionary<string,object> view=new Dictionary<string,object>{{"kind","create_sweep"},{"revision_id",binding.RevisionId},{"sketch_id",plan.SketchId},
            {"path_curve_ids",data.Request.PathCurveIds},{"path_points_mm",points},{"profile_output",source.ProfileOutput},
            {"orientation_mode",source.OrientationMode},{"twist_angle_deg",source.TwistAngleDeg},{"profile_analysis",analysis},
            {"supported_domain","One active Line polygon or chain; ordered, tangent-continuous independent Line/Arc path from sketch origin at a unique profile vertex. Geometry verification uses finite samples."}};
        return new M3PlanSnapshot{Kind="create_sweep",RevisionId=binding.RevisionId,SketchId=plan.SketchId,PublicView=view,AdapterState=plan};
    }

    private static Dictionary<string,object> F21Read(Part part,M3PlanState plan,Swept feature)
    {
        try { return V2SweepFactory.ReadIsolated(Session.GetSession(),part,feature,plan.Sketch,plan.F21Sweep.Path,plan.F21Sweep.Points,
            plan.F21Sweep.ProfileFingerprint,delegate{return F19Inventory(part);}); }
        catch(V2SweepFactory.ReadStateUnknownException ex){throw M3AnalysisStateUnknown("sweep reader could not restore state",ex);}
    }

    private static bool F21DefinitionMatches(M3PlanState plan,Dictionary<string,object> definition)
    {
        if(definition==null)return false;
        F21SweepPlanData d=plan.F21Sweep; object value;
        if(!definition.TryGetValue("feature_id",out value) || !Object.Equals(value,plan.FeatureId) ||
           !definition.TryGetValue("sketch_feature_id",out value) || !Object.Equals(value,RequiredJournalId(plan.Sketch.Feature,"sweep sketch feature")) ||
           !definition.TryGetValue("path_curve_ids",out value) || M2Json.CanonicalText(value)!=M2Json.CanonicalText(d.Request.PathCurveIds) ||
           !definition.TryGetValue("profile_output",out value) || !Object.Equals(value,d.Request.ProfileOutput) ||
           !definition.TryGetValue("orientation_mode",out value) || !Object.Equals(value,d.Request.OrientationMode) ||
           !definition.TryGetValue("requested_twist_deg",out value) || value==null)return false;
        double twist;
        try{twist=Convert.ToDouble(value,CultureInfo.InvariantCulture);}catch(Exception){return false;}
        return IsFinite(twist) && Math.Abs(twist-d.Request.TwistAngleDeg)<=1.0e-9;
    }

    // Pure diagnostic comparison only; the authoritative acceptance test is
    // still F16DefinitionsEqual and retains its existing short-circuit order.
    private static Dictionary<string,object> F21FirstDefinitionDifference(object before,object after,string path)
    {
        if(String.Equals(M2Json.CanonicalText(before),M2Json.CanonicalText(after),StringComparison.Ordinal))return null;
        Dictionary<string,object> left=before as Dictionary<string,object>,right=after as Dictionary<string,object>;
        if(left!=null && right!=null)
        {
            HashSet<string> all=new HashSet<string>(left.Keys,StringComparer.Ordinal);all.UnionWith(right.Keys);
            List<string> keys=new List<string>(all);keys.Sort(StringComparer.Ordinal);
            foreach(string key in keys)
            {
                object a,b;bool hasA=left.TryGetValue(key,out a),hasB=right.TryGetValue(key,out b);
                string child=path+"/"+key.Replace("~","~0").Replace("/","~1");
                if(!hasA || !hasB)return new Dictionary<string,object>{{"path",child},{"before_present",hasA},{"after_present",hasB},
                    {"before",hasA?a:null},{"after",hasB?b:null}};
                Dictionary<string,object> difference=F21FirstDefinitionDifference(a,b,child);
                if(difference!=null)return difference;
            }
        }
        System.Collections.IList oldList=before as System.Collections.IList,newList=after as System.Collections.IList;
        if(oldList!=null && newList!=null)
        {
            int count=Math.Max(oldList.Count,newList.Count);
            for(int i=0;i<count;i++)
            {
                bool hasA=i<oldList.Count,hasB=i<newList.Count;
                string child=path+"/"+i.ToString(CultureInfo.InvariantCulture);
                if(!hasA || !hasB)return new Dictionary<string,object>{{"path",child},{"before_present",hasA},{"after_present",hasB},
                    {"before",hasA?oldList[i]:null},{"after",hasB?newList[i]:null}};
                Dictionary<string,object> difference=F21FirstDefinitionDifference(oldList[i],newList[i],child);
                if(difference!=null)return difference;
            }
        }
        return new Dictionary<string,object>{{"path",path},{"before_present",true},{"after_present",true},{"before",before},{"after",after}};
    }

    private static void F21AddDefinitionDifference(Dictionary<string,object> diagnostic,object before,object after)
    {
        diagnostic["before"]=before;diagnostic["after"]=after;
        try
        {
            Dictionary<string,object> difference=F21FirstDefinitionDifference(before,after,"");
            diagnostic["firstdiff"]=difference ?? new Dictionary<string,object>{{"path",""},{"reason","null dictionary is not accepted by the comparison"},
                {"before_present",true},{"after_present",true},{"before",before},{"after",after}};
        }
        catch(Exception ex){diagnostic["diagnostic_error"]=ex.GetType().FullName;}
    }

    private static bool F21Preserved(Part part,M3PlanState plan,Dictionary<string,object> snapshot,List<M3BodyMeasure> bodies,
        Dictionary<string,string> prints,Dictionary<string,Dictionary<string,object>> details,Swept feature,out Dictionary<string,object> evidence)
    {
        F21SweepPlanData d=plan.F21Sweep;
        Dictionary<string,string> oldPrints=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string tag in plan.BodyFingerprintsBefore.Keys){string v;if(prints.TryGetValue(tag,out v))oldPrints.Add(tag,v);}
        Dictionary<string,object> bodyEvidence;
        bool priorBodies=F19BodyPrintsCompare(plan.BodyFingerprintsBefore,oldPrints,d.BodyDetailsBefore,details,d.OldMirrorBodyTags,out bodyEvidence);
        bool bodyCount=bodies.Count==plan.BodiesBefore.Count+1;
        Dictionary<string,string> before=new Dictionary<string,string>(plan.F18StableBefore,StringComparer.Ordinal);
        Dictionary<string,string> after=F18StableInventory(part,null);
        foreach(string key in new List<string>(before.Keys))if(key.StartsWith("C:",StringComparison.Ordinal)){before.Remove(key);after.Remove(key);}
        List<object> differences;bool stable=F18StableSubset(before,after,out differences);
        HashSet<string> parents=new HashSet<string>(StringComparer.Ordinal);
        if(feature!=null){Feature[] p=feature.GetParents();if(p==null)stable=false;else foreach(Feature f in p)if(f!=null)parents.Add(f.Tag.ToString());}
        Dictionary<string,List<string>> children=F21Children(part);bool childrenMatch=true;
        foreach(KeyValuePair<string,List<string>> old in d.ChildrenBefore)
        {
            List<string> actual; if(!children.TryGetValue(old.Key,out actual)){childrenMatch=false;continue;}
            HashSet<string> expected=new HashSet<string>(old.Value,StringComparer.Ordinal);
            if(feature!=null && parents.Contains(old.Key))expected.Add(feature.Tag.ToString());
            if(!expected.SetEquals(actual) || actual.Count!=expected.Count)childrenMatch=false;
        }
        HashSet<string> created=new HashSet<string>(StringComparer.Ordinal);
        foreach(Feature f in part.Features)if(!plan.F18StableBefore.ContainsKey("F:"+f.Tag))created.Add(f.Tag.ToString());
        bool owned=feature!=null && created.SetEquals(new string[]{feature.Tag.ToString()});
        foreach(NXOpen.Point p in part.Points.ToArray())if(!plan.F18StableBefore.ContainsKey("P:"+p.Tag))owned=false;
        foreach(Direction x in part.Directions.ToArray())if(!plan.F18StableBefore.ContainsKey("D:"+x.Tag))owned=false;
        foreach(Axis x in part.Axes.ToArray())if(!plan.F18StableBefore.ContainsKey("A:"+x.Tag))owned=false;
        HashSet<string> outputExpressions=new HashSet<string>(StringComparer.Ordinal);
        if(feature!=null){Expression[] expressions=feature.GetExpressions();if(expressions==null)owned=false;else foreach(Expression e in expressions)if(e!=null)outputExpressions.Add(e.Tag.ToString());}
        foreach(Expression e in part.Expressions)if(!plan.F18StableBefore.ContainsKey("E:"+e.Tag) && !outputExpressions.Contains(e.Tag.ToString()))owned=false;
        bool curves=String.Equals(plan.F12CurveInventoryBefore,M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(part)),StringComparison.Ordinal);
        bool sketches=ExactProjection(plan.F12SemanticBefore,snapshot,"sketches",null) && ExactProjection(plan.F12SemanticBefore,snapshot,"native_helical_definitions",null);
        Dictionary<string,object> priorDefinitionDiagnostics=new Dictionary<string,object>(StringComparer.Ordinal);
        Dictionary<string,object> f19Diagnostic=new Dictionary<string,object>{{"status","NOT_EVALUATED"}};
        Dictionary<string,object> sweptDiagnostic=new Dictionary<string,object>{{"status","NOT_EVALUATED"}};
        Dictionary<string,object> seedDiagnostic=new Dictionary<string,object>{{"status","NOT_EVALUATED"}};
        Dictionary<string,object> datumDiagnostic=new Dictionary<string,object>{{"status","NOT_EVALUATED"}};
        priorDefinitionDiagnostics.Add("f19_related",f19Diagnostic);priorDefinitionDiagnostics.Add("prior_swept",sweptDiagnostic);
        priorDefinitionDiagnostics.Add("seed",seedDiagnostic);priorDefinitionDiagnostics.Add("sketch_datum",datumDiagnostic);
        bool definitions=F19DefinitionsMatch(part,plan);
        f19Diagnostic["status"]=definitions?"VERIFIED":"MISMATCH";
        if(definitions)
        {
            Dictionary<string,object> current=F21CaptureDefinitions(part,plan.FeatureId);
            Dictionary<string,object> comparison;
            definitions=F21SweepDefinitionComparison.Compare(d.PriorDefinitions,current,out comparison);
            sweptDiagnostic["comparison"]=comparison;
            sweptDiagnostic["status"]=definitions?"VERIFIED":"MISMATCH";
            if(!definitions)F21AddDefinitionDifference(sweptDiagnostic,d.PriorDefinitions,current);
        }
        if(definitions)
        {
            Dictionary<string,object> current=F21CaptureSeedDefinitions(part);
            definitions=F16DefinitionsEqual(d.SeedDefinitions,current);
            seedDiagnostic["status"]=definitions?"VERIFIED":"MISMATCH";
            if(!definitions)F21AddDefinitionDifference(seedDiagnostic,d.SeedDefinitions,current);
        }
        if(definitions)
        {
            Dictionary<string,object> current=F21CaptureSketchDatums(part);
            definitions=F16DefinitionsEqual(d.SketchDatumDefinitions,current);
            datumDiagnostic["status"]=definitions?"VERIFIED":"MISMATCH";
            if(!definitions)F21AddDefinitionDifference(datumDiagnostic,d.SketchDatumDefinitions,current);
        }
        bool planes=String.Equals(plan.F19PlanesBefore,F19PlaneInventory(part),StringComparison.Ordinal);
        bool datums=String.Equals(plan.F19DatumsBefore,F19DatumInventory(part),StringComparison.Ordinal);
        bool expressionsSame=true;Dictionary<string,object> nowExpressions=F19ExpressionStates(part);
        foreach(KeyValuePair<string,object> item in d.ExpressionsBefore)
        {object current;if(!nowExpressions.TryGetValue(item.Key,out current) || M2Json.CanonicalText(item.Value)!=M2Json.CanonicalText(current))expressionsSame=false;}
        bool ok=priorBodies && bodyCount && stable && childrenMatch && owned && curves && sketches && definitions && planes && datums && expressionsSame;
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"prior_bodies",bodyEvidence},{"body_count_matches",bodyCount},
            {"prior_inventory",stable},{"prior_inventory_differences",differences},{"prior_children",childrenMatch},{"new_object_ownership",owned},
            {"curve_inventory",curves},{"sketches_and_helical",sketches},{"prior_native_definitions",definitions},{"prior_definition_diagnostics",priorDefinitionDiagnostics},
            {"plane_inventory",planes},{"datum_inventory",datums},{"prior_expressions",expressionsSame}};
        return ok;
    }

    private M3ExecutionResult ExecuteF21SweepPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.F21Sweep==null || prepared.Kind!="create_sweep")
            return M3Execution("UNCHANGED",binding==null?null:binding.RevisionId,null,"INVALID_PLAN","sweep plan unavailable");
        BindingState state;try{state=RequireState(binding);}catch(M2ModelException ex){return M3Execution("UNCHANGED",binding.RevisionId,null,ex.Code,ex.Message);}
        string reason;if(!Object.ReferenceEquals(state,plan.BindingState) || prepared.RevisionId!=plan.RevisionId || !IsBindingCurrent(binding,out reason) || binding.RevisionId!=plan.RevisionId)
            return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","binding or revision changed");
        F21SweepPlanData d=plan.F21Sweep;
        try
        {
            RequireWritableSimplePart(state.WorkPart,true);RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm");
            if(state.Session.ActiveSketch!=null)throw new M2ModelException("NX_BUSY","end active sketch");
            SnapshotResult current=Capture(binding,state);List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);
            Dictionary<string,object> ignored;Dictionary<string,Dictionary<string,object>> details=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            Dictionary<string,string> prints=CaptureF12BodyFingerprints(state.WorkPart,bodies,details);
            Dictionary<string,object> priorSweepComparison;
            if(state.WorkPart.IsModified!=plan.WorkPartModifiedBefore || plan.F18InventoryBefore!=F19Inventory(state.WorkPart) ||
               M2Json.CanonicalText(d.ExpressionsBefore)!=M2Json.CanonicalText(F19ExpressionStates(state.WorkPart)) ||
               plan.SemanticBefore!=CanonicalSemanticSnapshot(current.Snapshot) || !BodyMeasuresEqual(plan.BodiesBefore,bodies,out ignored) ||
               !F19BodyPrintsCompare(plan.BodyFingerprintsBefore,prints,d.BodyDetailsBefore,details,d.OldMirrorBodyTags,out ignored) ||
               !F19DefinitionsMatch(state.WorkPart,plan) || !F21SweepDefinitionComparison.Compare(d.PriorDefinitions,F21CaptureDefinitions(state.WorkPart,null),out priorSweepComparison) ||
               !F16DefinitionsEqual(d.SeedDefinitions,F21CaptureSeedDefinitions(state.WorkPart)) ||
               !F16DefinitionsEqual(d.SketchDatumDefinitions,F21CaptureSketchDatums(state.WorkPart)))
                return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","sweep inputs or prior model state changed");
        }
        catch(Exception ex){M2ModelException unknown=F12AnalysisStateUnknownOrNull(ex,"sweep precondition cleanup failed");return unknown!=null?M3Execution("UNKNOWN",binding.RevisionId,null,unknown.Code,unknown.Message):M3Execution("UNCHANGED",binding.RevisionId,null,"MODEL_READ_FAILED","sweep precondition read failed ("+ex.GetType().Name+")");}
        Session.UndoMarkId mark;try{mark=state.Session.SetUndoMark(Session.MarkVisibility.Invisible,"F21 native sweep transaction");}
        catch(Exception ex){return M3Execution("UNCHANGED",binding.RevisionId,null,"UPDATE_FAILED","undo mark failed ("+ex.GetType().Name+")");}
        try
        {
            V2SweepFactory.NativeResult native=V2SweepFactory.Create(state.WorkPart,plan.Sketch,d.Path,d.Points,d.Request.ProfileOutput,d.Request.OrientationMode,d.Request.TwistAngleDeg,d.ProfileFingerprint);
            Swept feature=native.Feature;plan.Feature=feature;plan.FeatureId=RequiredJournalId(feature,"sweep feature");
            if(state.Session.UpdateManager.DoUpdate(mark)!=0)throw new M2ModelException("M3_UPDATE_FAILED","sweep update failed");
            Dictionary<string,object> definition=F21Read(state.WorkPart,plan,feature);
            if(!F21DefinitionMatches(plan,definition))throw new M2ModelException("F21_DEFINITION_MISMATCH","native sweep differs from request");
            object bodyId;if(!definition.TryGetValue("body_id",out bodyId) || !(bodyId is string))throw new M2ModelException("F21_DEFINITION_MISMATCH","sweep body identity missing");
            Body body=FindBody(state.WorkPart,(string)bodyId);
            Dictionary<string,object> geometry=V2SweepGeometry.Verify(state.WorkPart,plan.Sketch,d.Path,d.Points,d.Request.OrientationMode,d.Request.TwistAngleDeg,d.Request.ProfileOutput,body);
            bool coverage=geometry!=null && Object.Equals(geometry["status"],"SAMPLED_GEOMETRY_AND_TOPOLOGY_MATCH");
            SnapshotResult after=Capture(binding,state);plan.BodiesAfter=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);
            d.BodyDetailsAfter=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            plan.BodyFingerprintsAfter=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesAfter,d.BodyDetailsAfter);
            Dictionary<string,object> preservation;bool preserved=F21Preserved(state.WorkPart,plan,after.Snapshot,plan.BodiesAfter,plan.BodyFingerprintsAfter,d.BodyDetailsAfter,feature,out preservation);
            List<object> support;bool supportOk=ValidateM4SupportTracks(state,out support,null);
            plan.ExecutionFailureEvidence=new Dictionary<string,object>{{"native_definition",definition},{"geometry",geometry},{"model_preservation",preservation},{"support_associations",support}};
            if(!coverage || !preserved || !supportOk)throw new M2ModelException("F21_POSTCONDITION_FAILED","sweep geometry, association or preservation failed");
            if(plan.ForcePostCommitVerificationFailureForTesting)throw new M2ModelException("F21_FORCED_POST_COMMIT_FAILURE","test-only sweep post-commit failure");
            d.DefinitionAfter=definition;d.GeometryAfter=geometry;d.ExpressionsAfter=F19ExpressionStates(state.WorkPart);
            plan.SemanticAfter=CanonicalSemanticSnapshot(after.Snapshot);plan.GeometryAfter=CaptureFullGeometry(state.WorkPart);plan.HistoryAfter=HistoryProjection(after.Snapshot);
            Dictionary<string,object> result=new Dictionary<string,object>{{"kind","create_sweep"},{"feature_id",plan.FeatureId},{"native_definition",definition},
                {"geometry",geometry},{"model_preservation",preservation},{"revision_id",binding.RevisionId}};
            plan.Execution=M3Execution("APPLIED",binding.RevisionId,result,null,null);return plan.Execution;
        }
        catch(Exception ex)
        {
            V2SweepGeometry.GeometryMismatchException mismatch=ex as V2SweepGeometry.GeometryMismatchException;
            if(mismatch!=null)
            {
                if(plan.ExecutionFailureEvidence==null)
                    plan.ExecutionFailureEvidence=new Dictionary<string,object>(StringComparer.Ordinal);
                plan.ExecutionFailureEvidence["geometry_mismatch"]=new Dictionary<string,object>(StringComparer.Ordinal)
                {
                    {"reason",mismatch.Message},
                    {"details",mismatch.Evidence==null?null:new Dictionary<string,object>(mismatch.Evidence,StringComparer.Ordinal)}
                };
            }
            bool cleanupUnknown=ex is V2SweepGeometry.CleanupException || ex is V2SweepFactory.ReadStateUnknownException ||
                (ex is M2ModelException && ((M2ModelException)ex).Code=="M3_ANALYSIS_STATE_UNKNOWN");
            M3ExecutionResult rolled=RestoreM3State(binding,state,plan,mark,ex);
            plan.Execution=cleanupUnknown?M3Execution("UNKNOWN",binding.RevisionId,null,"M3_ANALYSIS_STATE_UNKNOWN","sweep cleanup state could not be proven"):rolled;
            return plan.Execution;
        }
        finally{try{state.Session.DeleteUndoMark(mark,"F21 native sweep transaction");}catch(Exception ex){throw M3AnalysisStateUnknown("sweep transaction mark cleanup failed",ex);}}
    }

    private M3VerificationResult VerifyF21SweepPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.F21Sweep==null || plan.Execution==null || plan.Execution.State!="APPLIED")
            return M3VerificationFailure(binding==null?null:binding.RevisionId,"fresh sweep verification requires an applied plan");
        BindingState state=RequireState(binding);string reason;
        if(!Object.ReferenceEquals(state,plan.BindingState) || !IsBindingCurrent(binding,out reason))return M3VerificationFailure(binding.RevisionId,"sweep binding changed");
        F21SweepPlanData d=plan.F21Sweep;Swept feature=FindFeature(state.WorkPart,plan.FeatureId) as Swept;
        if(feature==null)return M3VerificationFailure(binding.RevisionId,"sweep feature is missing");
        Dictionary<string,object> definition=F21Read(state.WorkPart,plan,feature);object bodyId;
        if(!definition.TryGetValue("body_id",out bodyId) || !(bodyId is string))return M3VerificationFailure(binding.RevisionId,"sweep body is missing");
        Body body=FindBody(state.WorkPart,(string)bodyId);Dictionary<string,object> geometry;
        try
        {
            geometry=F21AuditRead(state.WorkPart,delegate
            {return V2SweepGeometry.Verify(state.WorkPart,plan.Sketch,d.Path,d.Points,d.Request.OrientationMode,d.Request.TwistAngleDeg,d.Request.ProfileOutput,body);});
        }
        catch(V2SweepGeometry.GeometryMismatchException ex)
        {
            if(ex.Evidence==null)throw M3AnalysisStateUnknown("fresh sweep mismatch has no structured geometry evidence",ex);
            // Capture the externally edited state only after the geometry read's
            // audit succeeds. Audit the capture itself; never undo a user edit.
            try {F21AuditRead(state.WorkPart,delegate{return Capture(binding,state);});}
            catch(Exception captureError){throw M3AnalysisStateUnknown("fresh sweep mismatch revision capture could not be proven read-only",captureError);}
            Dictionary<string,object> mismatch=new Dictionary<string,object>(StringComparer.Ordinal)
            {
                {"reason",ex.Message}, {"details",new Dictionary<string,object>(ex.Evidence,StringComparer.Ordinal)}
            };
            return new M3VerificationResult
            {
                Verified=false,RevisionId=binding.RevisionId,
                Checks=new Dictionary<string,object>{{"sweep_geometry",VerificationCheck("MISMATCH",mismatch)}},
                Result=new Dictionary<string,object>{{"kind","create_sweep"},{"feature_id",plan.FeatureId},{"geometry",mismatch}},
                ErrorCode="PART_VERIFICATION_FAILED",ErrorMessage="Fresh sweep geometry differs: "+ex.Message
            };
        }
        catch(Exception ex){throw M3AnalysisStateUnknown("fresh sweep geometry read or cleanup could not be classified safely",ex);}
        SnapshotResult capture=Capture(binding,state);List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);
        Dictionary<string,Dictionary<string,object>> details=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
        Dictionary<string,string> prints=CaptureF12BodyFingerprints(state.WorkPart,bodies,details);
        Dictionary<string,object> preservation,bodyEvidence,printEvidence;
        Dictionary<string,object> definitionComparison=new Dictionary<string,object>{{"status","NOT_EVALUATED"}};
        bool native=F21DefinitionMatches(plan,definition) && F21SweepDefinitionComparison.CompareDefinition(d.DefinitionAfter,definition,out definitionComparison);
        bool coverage=geometry!=null && Object.Equals(geometry["status"],"SAMPLED_GEOMETRY_AND_TOPOLOGY_MATCH");
        bool preserved=F21Preserved(state.WorkPart,plan,capture.Snapshot,bodies,prints,details,feature,out preservation);
        bool measured=BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter,null),BodyMeasureRecords(bodies,null),out bodyEvidence);
        // Only previously captured mirror bodies use the mirror-specific
        // comparator; the Swept output must match the strict F12 fingerprint.
        HashSet<string> allowed=new HashSet<string>(d.OldMirrorBodyTags,StringComparer.Ordinal);
        bool samePrints=F19BodyPrintsCompare(plan.BodyFingerprintsAfter,prints,d.BodyDetailsAfter,details,allowed,out printEvidence);
        bool sameGeometry=M2Json.CanonicalText(d.GeometryAfter)==M2Json.CanonicalText(geometry);
        bool sameExpressions=M2Json.CanonicalText(d.ExpressionsAfter)==M2Json.CanonicalText(F19ExpressionStates(state.WorkPart));
        bool sameHistory=plan.SemanticAfter==CanonicalSemanticSnapshot(capture.Snapshot) && plan.GeometryAfter==CaptureFullGeometry(state.WorkPart) &&
            SemanticValuesEqual(plan.HistoryAfter,HistoryProjection(capture.Snapshot),"history");
        List<object> support;bool supportOk=ValidateM4SupportTracks(state,out support,null);
        bool verified=native && coverage && preserved && measured && samePrints && sameGeometry && sameExpressions && sameHistory && supportOk;
        Dictionary<string,object> definitionEvidence=new Dictionary<string,object>(definition,StringComparer.Ordinal);
        definitionEvidence["definition_comparison"]=definitionComparison;
        Dictionary<string,object> checks=new Dictionary<string,object>{{"sweep_definition",VerificationCheck(native?"VERIFIED":"MISMATCH",definitionEvidence)},
            {"sweep_geometry",VerificationCheck(coverage?"VERIFIED":"MISMATCH",geometry)},
            {"model_preservation",VerificationCheck(preserved?"VERIFIED":"MISMATCH",preservation)},
            {"body_effect",VerificationCheck(coverage && preserved?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches",coverage && preserved}})},
            {"geometry",VerificationCheck(sameGeometry && sameExpressions?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches_execute",sameGeometry},{"expression_state_matches_execute",sameExpressions}})},
            {"bodies",VerificationCheck(measured && samePrints?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"measurements",bodyEvidence},{"fingerprints",printEvidence}})},
            {"history",VerificationCheck(sameHistory?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches_execute",sameHistory}})},
            {"support_associations",VerificationCheck(supportOk?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"associations",support}})}};
        return new M3VerificationResult{Verified=verified,RevisionId=binding.RevisionId,Checks=checks,
            Result=new Dictionary<string,object>{{"kind","create_sweep"},{"feature_id",plan.FeatureId},{"native_definition",definition},{"geometry",geometry}},
            ErrorCode=verified?null:"PART_VERIFICATION_FAILED",ErrorMessage=verified?null:"Fresh sweep definition, sampled geometry or preservation differs."};
    }
}
