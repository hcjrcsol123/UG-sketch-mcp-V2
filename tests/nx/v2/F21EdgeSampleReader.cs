// F21 independent native edge sample capture. No NX startup or product imports.
using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;

public static class F21EdgeSampleReader
{
    public static Dictionary<string,object> Read(Body body,double[] target)
    {
        if(body==null || target==null || target.Length!=3)throw new ArgumentException("body and target required");
        Part part=body.OwningPart as Part;if(part==null)throw new InvalidOperationException("body part unavailable");bool dirty=part.IsModified;
        foreach(double v in target)if(Double.IsNaN(v)||Double.IsInfinity(v))throw new ArgumentException("nonfinite target");
        List<Dictionary<string,object>> candidates=new List<Dictionary<string,object>>();UFSession uf=UFSession.GetUFSession();
        foreach(Edge edge in body.GetEdges())
        {
            IntPtr evaluator=IntPtr.Zero;
            try{
                uf.Eval.Initialize2(edge.Tag,out evaluator);if(evaluator==IntPtr.Zero)throw new InvalidOperationException("missing evaluator");
                double[] limits=new double[2],nearest=new double[3],point=new double[3],tangent=new double[3];double parameter;
                uf.Eval.AskLimits(evaluator,limits);uf.Eval.EvaluateClosestPoint(evaluator,target,out parameter,nearest);
                if(part.IsModified!=dirty)throw new InvalidOperationException("UNKNOWN edge closest-point changed dirty state");
                uf.Eval.Evaluate(evaluator,1,parameter,point,tangent);
                if(part.IsModified!=dirty)throw new InvalidOperationException("UNKNOWN edge evaluation changed dirty state");
                double a=0,b=0;for(int i=0;i<3;i++){a+=(point[i]-target[i])*(point[i]-target[i]);b+=(point[i]-nearest[i])*(point[i]-nearest[i]);}
                candidates.Add(new Dictionary<string,object>{{"edge_id",edge.JournalIdentifier},{"edge_tag",edge.Tag.ToString()},
                    {"parameter",parameter},{"limits",limits},{"closest_mm",nearest},{"point_mm",point},{"tangent",tangent},
                    {"target_residual_mm",Math.Sqrt(a)},{"evaluation_projection_error_mm",Math.Sqrt(b)}});
            } finally {if(evaluator!=IntPtr.Zero)try{uf.Eval.Free(evaluator);}catch(Exception ex){throw new InvalidOperationException("UNKNOWN edge evaluator cleanup",ex);}}
        }
        candidates.Sort(delegate(Dictionary<string,object> a,Dictionary<string,object> b){return ((double)a["target_residual_mm"]).CompareTo((double)b["target_residual_mm"]);});
        if(candidates.Count==0 || (double)candidates[0]["target_residual_mm"]>0.02 || (double)candidates[0]["evaluation_projection_error_mm"]>1e-6)
            throw new InvalidOperationException("no native edge covers sample within predeclared limit");
        return new Dictionary<string,object>{{"target_mm",target},{"selected",candidates[0]},{"candidates",candidates},
            {"method","UF edge EvaluateClosestPoint and independent Evaluate; coincident shared edges retained"}};
    }
}
