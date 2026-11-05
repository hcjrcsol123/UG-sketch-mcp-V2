using System;
using System.Collections;
using System.Collections.Generic;

public sealed partial class M2OpenModelAdapter
{
    private const double F19UvEpsilon=2.2204460492503131e-16;
    private const double F19UvFactor=64.0*F19UvEpsilon;

    private static bool F19Vector(object value,int length,out double[] vector)
    {
        vector=value as double[];if(vector==null || vector.Length!=length)return false;
        foreach(double x in vector)if(!IsFinite(x))return false;return true;
    }
    private static double F19Dot(double[] a,double[] b){return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];}
    private static double F19Norm(double[] a){return Math.Sqrt(F19Dot(a,a));}
    private static double[] F19Cross(double[] a,double[] b){return new double[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};}
    private static bool F19UvNear(double a,double b)
    {return IsFinite(a) && IsFinite(b) && Math.Abs(a-b)<=F19UvFactor*Math.Max(1.0,Math.Max(Math.Abs(a),Math.Abs(b)));}
    private static double F19PointTolerance(double scale){return Math.Min(1e-7,F19UvFactor*Math.Max(1.0,scale));}
    private static bool F19HealthyBody(Dictionary<string,object> detail)
    {
        if(detail==null || !detail.ContainsKey("consistency_fault_count") || !Object.Equals(detail["consistency_fault_count"],0))return false;
        object types,tags;if(!detail.TryGetValue("consistency_fault_types",out types) || !detail.TryGetValue("consistency_fault_tags",out tags))return false;
        IList typeList=types as IList,tagList=tags as IList;return typeList!=null && tagList!=null && typeList.Count==0 && tagList.Count==0;
    }

    // The caller separately proves the native Mirror association and complete
    // fixed-box geometry. Here each native planar grid is checked independently,
    // then only bounded UV and point changes are removed from exact comparison.
    private static bool F19UvFacePair(Dictionary<string,object> left,Dictionary<string,object> right,double bodyScale,
        out Dictionary<string,object> normalizedLeft,out Dictionary<string,object> normalizedRight,out Dictionary<string,object> evidence)
    {
        normalizedLeft=null;normalizedRight=null; evidence=new Dictionary<string,object>();
        if(left==null || right==null || !left.ContainsKey("tag") || !right.ContainsKey("tag") || !Object.Equals(left["tag"],right["tag"]) ||
            !left.ContainsKey("native_type") || !right.ContainsKey("native_type") || !Object.Equals(left["native_type"],22) || !Object.Equals(right["native_type"],22))return false;
        object lv,rv;double[] leftUv,rightUv,leftOrigin,rightOrigin,leftDirection,rightDirection;
        if(!left.TryGetValue("uv_limits",out lv) || !right.TryGetValue("uv_limits",out rv) || !F19Vector(lv,4,out leftUv) || !F19Vector(rv,4,out rightUv) ||
            !left.TryGetValue("origin_mm",out lv) || !right.TryGetValue("origin_mm",out rv) || !F19Vector(lv,3,out leftOrigin) || !F19Vector(rv,3,out rightOrigin) ||
            !left.TryGetValue("direction",out lv) || !right.TryGetValue("direction",out rv) || !F19Vector(lv,3,out leftDirection) || !F19Vector(rv,3,out rightDirection))return false;
        if(!(leftUv[0]<leftUv[1] && leftUv[2]<leftUv[3] && rightUv[0]<rightUv[1] && rightUv[2]<rightUv[3]))return false;
        for(int i=0;i<4;i++)if(!F19UvNear(leftUv[i],rightUv[i]))return false;
        if(!left.TryGetValue("surface_samples",out lv) || !right.TryGetValue("surface_samples",out rv))return false;
        List<object> ls=lv as List<object>,rs=rv as List<object>;if(ls==null || rs==null || ls.Count!=25 || rs.Count!=25)return false;
        object lsense,rsense;if(!left.TryGetValue("normal_sense",out lsense) || !right.TryGetValue("normal_sense",out rsense) ||
            !(lsense is int) || !(rsense is int) || Math.Abs((int)lsense)!=1 || Math.Abs((int)rsense)!=1)return false;
        Dictionary<string,object>[] faces=new Dictionary<string,object>[] {left,right};List<object>[] source=new List<object>[] {ls,rs};
        double[][] limits=new double[][]{leftUv,rightUv},origins=new double[][]{leftOrigin,rightOrigin},directions=new double[][]{leftDirection,rightDirection};
        List<object>[] normalized=new List<object>[] {new List<object>(),new List<object>()};
        double maxPointDelta=0,maxUvDelta=0,maxPlane=0,maxAffine=0,maxNormalError=0,maxTangentError=0;
        for(int i=0;i<4;i++)maxUvDelta=Math.Max(maxUvDelta,Math.Abs(leftUv[i]-rightUv[i]));
        for(int side=0;side<2;side++)
        {
            Dictionary<string,object> first=source[side][0] as Dictionary<string,object>;double[] baseUv,basePoint;
            if(first==null || !first.TryGetValue("uv",out lv) || !F19Vector(lv,2,out baseUv) || !first.TryGetValue("point_mm",out lv) || !F19Vector(lv,3,out basePoint))return false;
            double[] origin=origins[side],direction=directions[side];double dlen=F19Norm(direction);
            if(!IsFinite(dlen) || dlen<=0 || Math.Abs(dlen-1)>F19UvFactor)return false;
            int sense=(int)(side==0?lsense:rsense);
            for(int i=0;i<25;i++)
            {
                Dictionary<string,object> sample=source[side][i] as Dictionary<string,object>;double[] uv,point,normal,du,dv;
                if(sample==null || !sample.TryGetValue("uv",out lv) || !F19Vector(lv,2,out uv) ||
                    !sample.TryGetValue("point_mm",out lv) || !F19Vector(lv,3,out point) ||
                    !sample.TryGetValue("normal",out lv) || !F19Vector(lv,3,out normal) ||
                    !sample.TryGetValue("du",out lv) || !F19Vector(lv,3,out du) ||
                    !sample.TryGetValue("dv",out lv) || !F19Vector(lv,3,out dv))return false;
                double expectedU=limits[side][0]+(limits[side][1]-limits[side][0])*(i/5)/4.0;
                double expectedV=limits[side][2]+(limits[side][3]-limits[side][2])*(i%5)/4.0;
                if(uv[0]!=expectedU || uv[1]!=expectedV)return false;
                double scale=bodyScale;for(int j=0;j<3;j++)scale=Math.Max(scale,Math.Max(Math.Abs(point[j]),Math.Abs(origin[j])));
                double pointTolerance=F19PointTolerance(scale), nlen=F19Norm(normal),ulen=F19Norm(du),vlen=F19Norm(dv),crosslen=F19Norm(F19Cross(du,dv));
                if(!IsFinite(nlen) || !IsFinite(ulen) || !IsFinite(vlen) || !IsFinite(crosslen) || ulen<=1e-12 || vlen<=1e-12 || crosslen<=1e-12 || Math.Abs(nlen-1)>F19UvFactor)return false;
                double normalError=Math.Abs(1.0-F19Dot(normal,direction)/(nlen*dlen));
                double tangency=Math.Max(Math.Abs(F19Dot(normal,du))/(nlen*ulen),Math.Abs(F19Dot(normal,dv))/(nlen*vlen));
                double crossError=Math.Abs(1.0-sense*F19Dot(normal,F19Cross(du,dv))/(nlen*crosslen));
                if(!IsFinite(normalError) || !IsFinite(tangency) || !IsFinite(crossError) || normalError>F19UvFactor || tangency>F19UvFactor || crossError>F19UvFactor)return false;
                double[] delta=new double[3],affine=new double[3];for(int j=0;j<3;j++) {delta[j]=point[j]-origin[j];affine[j]=point[j]-(basePoint[j]+du[j]*(uv[0]-baseUv[0])+dv[j]*(uv[1]-baseUv[1]));}
                double plane=Math.Abs(F19Dot(delta,direction))/dlen,affineError=F19Norm(affine);
                if(!IsFinite(plane) || !IsFinite(affineError) || plane>pointTolerance || affineError>pointTolerance)return false;
                maxPlane=Math.Max(maxPlane,plane);maxAffine=Math.Max(maxAffine,affineError);maxNormalError=Math.Max(maxNormalError,Math.Max(normalError,crossError));maxTangentError=Math.Max(maxTangentError,tangency);
                Dictionary<string,object> sampleCopy=new Dictionary<string,object>(sample,StringComparer.Ordinal);
                sampleCopy["uv"]="<validated UV>";sampleCopy["point_mm"]="<validated planar point>";normalized[side].Add(sampleCopy);
                if(side==1)
                {
                    Dictionary<string,object> other=source[0][i] as Dictionary<string,object>;double[] priorUv,priorPoint;
                    if(other==null || !other.TryGetValue("uv",out lv) || !F19Vector(lv,2,out priorUv) || !other.TryGetValue("point_mm",out lv) || !F19Vector(lv,3,out priorPoint))return false;
                    for(int j=0;j<2;j++){if(!F19UvNear(priorUv[j],uv[j]))return false;maxUvDelta=Math.Max(maxUvDelta,Math.Abs(priorUv[j]-uv[j]));}
                    double[] displacement=new double[3];for(int j=0;j<3;j++){displacement[j]=point[j]-priorPoint[j];scale=Math.Max(scale,Math.Abs(priorPoint[j]));}
                    double pd=F19Norm(displacement);if(!IsFinite(pd) || pd>F19PointTolerance(scale))return false;maxPointDelta=Math.Max(maxPointDelta,pd);
                }
            }
        }
        normalizedLeft=new Dictionary<string,object>(left,StringComparer.Ordinal);normalizedRight=new Dictionary<string,object>(right,StringComparer.Ordinal);
        normalizedLeft["uv_limits"]="<validated UV limits>";normalizedRight["uv_limits"]="<validated UV limits>";
        normalizedLeft["surface_samples"]=normalized[0];normalizedRight["surface_samples"]=normalized[1];
        evidence=new Dictionary<string,object>{{"face_tag",left["tag"]},{"max_uv_delta",maxUvDelta},{"max_point_delta_mm",maxPointDelta},
            {"max_plane_residual_mm",maxPlane},{"max_affine_residual_mm",maxAffine},{"max_normal_error",maxNormalError},{"max_tangent_error",maxTangentError},{"body_scale_mm",bodyScale}};
        return true;
    }
    // F12 native snapshots contain multidimensional arrays and integral CLR
    // types outside the JSON transport's direct domain. Preserve every value
    // and array shape while materializing a JSON-compatible diagnostic record.
    private static object F19DiagnosticJson(object value)
    {
        if(value is double && !IsFinite((double)value))return ((double)value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(value==null || value is string || value is bool || value is int || value is long || value is double || value is decimal)return value;
        if(value is float)return (double)(float)value;
        if(value is short || value is ushort || value is byte || value is sbyte || value is uint)return Convert.ToInt64(value);
        IDictionary map=value as IDictionary;
        if(map!=null)
        {
            Dictionary<string,object> result=new Dictionary<string,object>(StringComparer.Ordinal);
            foreach(DictionaryEntry row in map)
            {if(!(row.Key is string))throw new InvalidOperationException("unsupported diagnostic map key");result.Add((string)row.Key,F19DiagnosticJson(row.Value));}
            return result;
        }
        Array array=value as Array;
        if(array!=null)
        {
            List<object> dimensions=new List<object>(),lowerBounds=new List<object>(),values=new List<object>();
            for(int i=0;i<array.Rank;i++){dimensions.Add(array.GetLength(i));lowerBounds.Add(array.GetLowerBound(i));}
            foreach(object item in array)values.Add(F19DiagnosticJson(item));
            return new Dictionary<string,object>{{"array_rank",array.Rank},{"dimensions",dimensions},{"lower_bounds",lowerBounds},{"values",values}};
        }
        IEnumerable sequence=value as IEnumerable;
        if(sequence!=null){List<object> result=new List<object>();foreach(object item in sequence)result.Add(F19DiagnosticJson(item));return result;}
        throw new InvalidOperationException("unsupported diagnostic value: "+value.GetType().Name);
    }

    private static bool F19AreaEqual(object left,object right,out long ulps)
    {
        ulps=-1;
        if(!(left is double) || !(right is double))return false;
        double a=(double)left,b=(double)right;
        if(!IsFinite(a) || !IsFinite(b) || a<0 || b<0)return false;
        long x=BitConverter.DoubleToInt64Bits(a),y=BitConverter.DoubleToInt64Bits(b);
        if(x<0 || y<0)return false;
        ulps=x>=y?x-y:y-x; return ulps<=8;
    }

    // Recursive exact comparison, with a single area exception in the output
    // instance's native face list. Other numeric fields remain bitwise-exact.
    private static bool F19GeometryCompare(object before,object after,string path,List<object> differences)
    {
        if(before==null || after==null)
        {if(before==after)return true; differences.Add(new Dictionary<string,object>{{"path",path},{"reason","null differs"}});return false;}
        IDictionary a=before as IDictionary,b=after as IDictionary;
        if(a!=null || b!=null)
        {
            if(a==null || b==null || a.Count!=b.Count){differences.Add(new Dictionary<string,object>{{"path",path},{"reason","map differs"}});return false;}
            bool same=true;foreach(DictionaryEntry item in a)
            {
                if(!(item.Key is string) || !b.Contains(item.Key)){differences.Add(new Dictionary<string,object>{{"path",path},{"reason","map key differs"}});same=false;continue;}
                string child=path+"/"+(string)item.Key;
                if(!F19GeometryCompare(item.Value,b[item.Key],child,differences))same=false;
            }
            return same;
        }
        Array arrayBefore=before as Array,arrayAfter=after as Array;
        if((arrayBefore!=null && arrayBefore.Rank>1) || (arrayAfter!=null && arrayAfter.Rank>1))
        {
            if(arrayBefore==null || arrayAfter==null || arrayBefore.Rank!=arrayAfter.Rank || arrayBefore.Length!=arrayAfter.Length)
            {differences.Add(new Dictionary<string,object>{{"path",path},{"reason","array shape differs"}});return false;}
            for(int d=0;d<arrayBefore.Rank;d++)if(arrayBefore.GetLength(d)!=arrayAfter.GetLength(d))
            {differences.Add(new Dictionary<string,object>{{"path",path},{"reason","array dimensions differ"}});return false;}
            IEnumerator ae=arrayBefore.GetEnumerator(),be=arrayAfter.GetEnumerator();bool same=true;int i=0;
            while(ae.MoveNext() && be.MoveNext()){if(!F19GeometryCompare(ae.Current,be.Current,path+"/flat/"+i,differences))same=false;i++;}
            return same;
        }
        IList x=before as IList,y=after as IList;
        if(x!=null || y!=null)
        {
            if(x==null || y==null || x.Count!=y.Count){differences.Add(new Dictionary<string,object>{{"path",path},{"reason","list differs"}});return false;}
            bool same=true;for(int i=0;i<x.Count;i++)if(!F19GeometryCompare(x[i],y[i],path+"/"+i,differences))same=false;
            return same;
        }
        string[] parts=path.Split('/'); int faceIndex;
        if(parts.Length==7 && parts[1]=="instances" && parts[2]=="0" && parts[3]=="native_geometry" && parts[4]=="faces" &&
            Int32.TryParse(parts[5],out faceIndex) && faceIndex>=0 && parts[6]=="area_mm2")
        {
            long ulps;bool same=F19AreaEqual(before,after,out ulps);
            differences.Add(new Dictionary<string,object>{{"path",path},{"before",F19DiagnosticJson(before)},{"after",F19DiagnosticJson(after)},{"ulp_distance",ulps},{"accepted",same}});return same;
        }
        bool exact=before.GetType()==after.GetType() && Object.Equals(before,after);
        if(exact && before is double)exact=BitConverter.DoubleToInt64Bits((double)before)==BitConverter.DoubleToInt64Bits((double)after);
        if(!exact)
        {differences.Add(new Dictionary<string,object>{{"path",path},{"reason","value differs"},{"before",F19DiagnosticJson(before)},
            {"after",F19DiagnosticJson(after)},{"before_type",before.GetType().FullName},{"after_type",after.GetType().FullName}});return false;}
        return true;
    }

    private static bool F19DetailedBodyCompare(Dictionary<string,object> before,Dictionary<string,object> after,out Dictionary<string,object> evidence)
    {
        List<object> areas=new List<object>(),uvEvidence=new List<object>();bool ok=before!=null && after!=null;
        Dictionary<string,object> left=before==null?null:new Dictionary<string,object>(before,StringComparer.Ordinal);
        Dictionary<string,object> right=after==null?null:new Dictionary<string,object>(after,StringComparer.Ordinal);
        object lfaces=null,rfaces=null;
        if(before!=null)before.TryGetValue("faces",out lfaces);if(after!=null)after.TryGetValue("faces",out rfaces);
        List<object> lf=lfaces as List<object>,rf=rfaces as List<object>;
        if(lf==null || rf==null || lf.Count!=6 || rf.Count!=6 || !F19HealthyBody(before) || !F19HealthyBody(after))ok=false;
        double bodyScale=1;
        if(lf!=null && rf!=null)foreach(object value in lf)
        {
            Dictionary<string,object> face=value as Dictionary<string,object>;object boxValue;double[] box;
            if(face==null || !face.TryGetValue("box_mm",out boxValue) || !F19Vector(boxValue,6,out box)){ok=false;continue;}
            foreach(double coordinate in box)bodyScale=Math.Max(bodyScale,Math.Abs(coordinate));
        }
        List<object> lc=new List<object>(),rc=new List<object>();
        if(lf!=null && rf!=null && lf.Count==rf.Count) for(int i=0;i<lf.Count;i++)
        {
            Dictionary<string,object> l=lf[i] as Dictionary<string,object>,r=rf[i] as Dictionary<string,object>;
            if(l==null || r==null || !l.ContainsKey("tag") || !r.ContainsKey("tag") || !l.ContainsKey("area_mm2") || !r.ContainsKey("area_mm2")) {ok=false;continue;}
            long distance=-1;bool area=Object.Equals(l["tag"],r["tag"]) && F19AreaEqual(l["area_mm2"],r["area_mm2"],out distance);
            if(!Object.Equals(l["tag"],r["tag"]))distance=-1;
            areas.Add(new Dictionary<string,object>{{"face_tag_before",l["tag"]},{"face_tag_after",r["tag"]},{"before",F19DiagnosticJson(l["area_mm2"])},{"after",F19DiagnosticJson(r["area_mm2"])},{"ulp_distance",distance},{"accepted",area}});
            if(!area)ok=false;
            Dictionary<string,object> lcopy,rcopy,faceUv;
            bool uv=F19UvFacePair(l,r,bodyScale,out lcopy,out rcopy,out faceUv);
            if(!uv){ok=false;lcopy=new Dictionary<string,object>(l,StringComparer.Ordinal);rcopy=new Dictionary<string,object>(r,StringComparer.Ordinal);}
            uvEvidence.Add(new Dictionary<string,object>{{"face_tag",l["tag"]},{"validated",uv},{"details",faceUv}});
            lcopy["area_mm2"]=0.0;rcopy["area_mm2"]=0.0;lc.Add(lcopy);rc.Add(rcopy);
        }
        if(left!=null)left["faces"]=lc;if(right!=null)right["faces"]=rc;
        bool remainder=ok && left!=null && right!=null && F12BodyGeometrySnapshot.Canonical(left)==F12BodyGeometrySnapshot.Canonical(right);
        List<object> differences=new List<object>();
        if(!remainder && left!=null && right!=null)
        {
            // Find a useful path for one-dimensional record fields; canonical
            // remains the authoritative exact comparison for all fields.
            try{F19GeometryCompare(left,right,"/body",differences);}catch(Exception){differences.Add(new Dictionary<string,object>{{"path","/body"},{"reason","non-area record differs"}});}
        }
        evidence=new Dictionary<string,object>{{"areas",areas},{"uv_validation",uvEvidence},{"non_area_record_exact_after_validated_uv",remainder},{"differences",differences}};
        return ok && remainder;
    }

    private static bool F19BodyPrintsCompare(Dictionary<string,string> before,Dictionary<string,string> after,
        Dictionary<string,Dictionary<string,object>> beforeDetails,Dictionary<string,Dictionary<string,object>> afterDetails,
        HashSet<string> allowed,out Dictionary<string,object> evidence)
    {
        Dictionary<string,object> strict;F12BodyFingerprintsEqual(before,after,out strict);
        List<object> details=new List<object>();bool ok=before!=null && after!=null && before.Count==after.Count;
        if(before!=null)foreach(KeyValuePair<string,string> row in before)
        {
            string current;if(after==null || !after.TryGetValue(row.Key,out current)){ok=false;continue;}
            bool mirrorDiagnostic=allowed!=null && allowed.Contains(row.Key);
            if(row.Value==current && !mirrorDiagnostic)continue;
            Dictionary<string,object> b=null,a=null,detail=null;
            if(beforeDetails!=null)beforeDetails.TryGetValue(row.Key,out b);
            if(afterDetails!=null)afterDetails.TryGetValue(row.Key,out a);
            bool permitted=mirrorDiagnostic && b!=null && a!=null && F19DetailedBodyCompare(b,a,out detail);
            if(detail==null)detail=new Dictionary<string,object>{{"reason","body is not an eligible verified mirror or details are missing"}};
            details.Add(new Dictionary<string,object>{{"body_tag",row.Key},{"accepted_area_only",permitted},{"comparison",detail},
                {"raw_before",!mirrorDiagnostic || b==null || row.Value==current?null:F19DiagnosticJson(b)},{"raw_after",!mirrorDiagnostic || a==null || row.Value==current?null:F19DiagnosticJson(a)}});
            if(!permitted)ok=false;
        }
        if(after!=null)foreach(string tag in after.Keys)if(before==null || !before.ContainsKey(tag))ok=false;
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"strict_f12",strict},{"differences",details}};return ok;
    }
}
