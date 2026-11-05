// Independent NX UF face-projection capture for F21 sample targets.
// Does not start NX or use production code; sole authorized operator calls it.
// Call only for specified side-face interior witnesses, not boundary vertices.
using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;

public static class F21FaceSampleReader
{
    public static Dictionary<string,object> Read(Body body,double[] target)
    {
        if(body==null || target==null || target.Length!=3) throw new ArgumentException("body and three-vector required");
        Part part=body.OwningPart as Part;if(part==null)throw new InvalidOperationException("body part unavailable");bool dirty=part.IsModified;
        foreach(double v in target) if(Double.IsNaN(v)||Double.IsInfinity(v)) throw new ArgumentException("nonfinite target");
        List<Dictionary<string,object>> candidates=new List<Dictionary<string,object>>();
        foreach(Face face in body.GetFaces())
        {
            double[] uv=new double[2],projection=new double[3],point=new double[3],du=new double[3],dv=new double[3];
            double[] duu=new double[3],dvv=new double[3],normal=new double[3],radii=new double[2];
            UFSession uf=UFSession.GetUFSession();
            uf.Modl.AskFaceParm(face.Tag,target,uv,projection);
            if(part.IsModified!=dirty)throw new InvalidOperationException("UNKNOWN face projection changed dirty state");
            uf.Modl.AskFaceProps(face.Tag,uv,point,du,dv,duu,dvv,normal,radii);
            if(part.IsModified!=dirty)throw new InvalidOperationException("UNKNOWN face evaluation changed dirty state");
            int containment;uf.Modl.AskPointContainment(point,face.Tag,out containment);
            if(part.IsModified!=dirty)throw new InvalidOperationException("UNKNOWN face containment changed dirty state");
            double projectionError=0,actualError=0;
            for(int i=0;i<3;i++){projectionError+=(projection[i]-point[i])*(projection[i]-point[i]);actualError+=(point[i]-target[i])*(point[i]-target[i]);}
            candidates.Add(new Dictionary<string,object>{{"face_id",face.JournalIdentifier},{"face_tag",face.Tag.ToString()},
                {"uv",uv},{"projected_mm",projection},{"point_mm",point},{"normal",normal},
                {"face_containment",containment},{"evaluation_projection_error_mm",Math.Sqrt(projectionError)},
                {"target_residual_mm",Math.Sqrt(actualError)}});
        }
        List<Dictionary<string,object>> eligible=candidates.FindAll(delegate(Dictionary<string,object> x){return (int)x["face_containment"]==1;});
        eligible.Sort(delegate(Dictionary<string,object> a,Dictionary<string,object> b){return ((double)a["target_residual_mm"]).CompareTo((double)b["target_residual_mm"]);});
        if(eligible.Count==0)throw new InvalidOperationException("no trimmed face contains projected sample");
        Dictionary<string,object> best=eligible[0];
        if((double)best["target_residual_mm"]>0.02 || (double)best["evaluation_projection_error_mm"]>1e-6)
            throw new InvalidOperationException("face sample projection outside predeclared tolerance");
        if(eligible.Count>1 && (double)eligible[1]["target_residual_mm"]-(double)best["target_residual_mm"]<0.05)
            throw new InvalidOperationException("sample face is ambiguous");
        return new Dictionary<string,object>{{"target_mm",target},{"selected",best},{"candidates",candidates},
            {"method","UF AskFaceParm, AskFaceProps, AskPointContainment; face interior only"}};
    }
}
