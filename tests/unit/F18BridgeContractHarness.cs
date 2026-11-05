// Focused bridge request parser checks, no NX process or bridge mutation.
using System;
using System.Collections.Generic;
using System.Reflection;
using NxSketchMcp.M2;

public static class F18BridgeContractHarness
{
    static MethodInfo parser=typeof(M2BridgeServer).GetMethod("ParseM3FeaturePatternRequest",BindingFlags.NonPublic|BindingFlags.Static);
    static int checks;
    static List<object> V(double a,double b,double c){return new List<object>{a,b,c};}
    static Dictionary<string,object> Base(string mode)
    {
        Dictionary<string,object> a=new Dictionary<string,object>{{"operation_id","op"},{"ownership_id","owner"},{"binding_epoch",1},
            {"revision_id","rev"},{"seed_feature_id","EXTRUDE(0)"},{"mode",mode},{"count1",3}};
        if(mode=="circular"){a.Add("axis_origin_mm",V(2,3,0));a.Add("axis_direction",V(0,0,1));a.Add("angle_deg",-70.0);}
        else{a.Add("direction1",V(-1,0,0));a.Add("pitch1_mm",8.0);if(mode=="rectangular"){a.Add("direction2",V(0,1,0));a.Add("pitch2_mm",11.0);a.Add("count2",2);}}
        return a;
    }
    static void Check(Dictionary<string,object> args,bool valid,string label)
    {
        bool accepted=false;object[] call={args,"rev","owner",1,null};
        try{M3FeaturePatternRequest request=(M3FeaturePatternRequest)parser.Invoke(null,call);accepted=request!=null && call[4]!=null;}
        catch(TargetInvocationException ex){if(ex.InnerException==null || ex.InnerException.GetType().Name!="BridgeException")throw;}
        checks++;if(accepted!=valid)throw new InvalidOperationException(label+" expected valid="+valid);
    }
    public static int Main()
    {
        if(parser==null)throw new InvalidOperationException("bridge parser unavailable");
        foreach(string mode in new string[]{"linear","rectangular","circular"})Check(Base(mode),true,mode);
        Dictionary<string,object> x=Base("linear");x["count1"]=true;Check(x,false,"boolean count");
        x=Base("linear");x["count1"]=1;Check(x,false,"count lower bound");
        x=Base("linear");x["count1"]=33;Check(x,false,"count upper bound");
        x=Base("linear");x["direction1"]=V(2,0,0);Check(x,false,"unit direction");
        x=Base("linear");x["pitch1_mm"]=0;Check(x,false,"zero pitch");
        x=Base("linear");x["angle_deg"]=1;Check(x,false,"mode-specific key");
        x=Base("rectangular");x["direction2"]=V(1,0,0);Check(x,false,"parallel directions");
        x=Base("rectangular");x["count1"]=17;Check(x,false,"grid product");
        x=Base("circular");x["angle_deg"]=-180;Check(x,false,"360-degree repeat");
        x=Base("circular");x["angle_deg"]=0;Check(x,false,"zero angle");
        x=Base("circular");x["axis_direction"]=V(0,0,double.PositiveInfinity);Check(x,false,"nonfinite axis");
        Console.WriteLine("F18 bridge parser checks passed: "+checks);return 0;
    }
}
