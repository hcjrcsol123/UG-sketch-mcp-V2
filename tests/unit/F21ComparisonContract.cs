// Offline F21 comparison contract against actual candidate assemblies and raw NX
// before/after definitions. No NX session is started. Mutations are synthetic.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using NxSketchMcp.M2;

public static class F21ComparisonContract
{
    static int checks;
    static JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};
    static void Need(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
    static Dictionary<string,object> D(object x){return (Dictionary<string,object>)x;}
    static IList L(object x){return (IList)x;}
    static object Clone(object value)
    {
        var d=value as Dictionary<string,object>;if(d!=null){var r=new Dictionary<string,object>(StringComparer.Ordinal);foreach(var x in d)r.Add(x.Key,Clone(x.Value));return r;}
        var l=value as IList;if(l!=null){var r=new List<object>();foreach(object x in l)r.Add(Clone(x));return r;}return value;
    }
    static Dictionary<string,object> Feature(Dictionary<string,object> data){return D(data.Values.Single());}
    static Dictionary<string,object> Section(Dictionary<string,object> data){return D(Feature(data)["section_evidence"]);}
    static Dictionary<string,object> Record(Dictionary<string,object> s,string collection,int i){return D(L(s[collection])[i]);}
    static Dictionary<string,object> Pair(Dictionary<string,object> s,int i){return D(L(D(s["section_domain_match"])["matched_geometry_pairs"])[i]);}
    static bool Compare(Dictionary<string,object> before,Dictionary<string,object> after,out Dictionary<string,object> evidence)
    {return F21SweepDefinitionComparison.Compare(before,after,out evidence);}
    static void Reject(string label,Dictionary<string,object> before,Dictionary<string,object> source,Action<Dictionary<string,object>> edit)
    {
        var test=D(Clone(source));edit(test);Dictionary<string,object> evidence;
        Need(!Compare(before,test,out evidence) && Object.Equals(evidence["status"],"MISMATCH"),label);
    }
    static Dictionary<string,object> Geometry(Dictionary<string,object> record){return D(record["geometry"]);}
    static Dictionary<string,object> FindSource(IList rows,string id)
    {foreach(object x in rows){var d=D(x);if(Object.Equals(d["source_jid"],id))return d;}throw new Exception("source missing");}
    static string Fingerprint(Dictionary<string,object> g)
    {
        MethodInfo method=typeof(V2SketchProfileAnalyzer).GetMethod("F12GeometryFingerprint",BindingFlags.NonPublic|BindingFlags.Static,null,new Type[]{typeof(Dictionary<string,object>)},null);
        return (string)method.Invoke(null,new object[]{g});
    }
    static string Domain(IList rows)
    {return V2SketchProfileAnalyzer.F12DomainFingerprint(rows.Cast<object>().Select(x=>D(x)));}
    static void SetGeometry(Dictionary<string,object> row,Dictionary<string,object> g)
    {row["geometry"]=Clone(g);row["geometry_fingerprint"]=Fingerprint(g);}
    static Dictionary<string,object> NearDuplicate(Dictionary<string,object> original,double delta)
    {
        var data=D(Clone(original));var section=Section(data);IList inputs=L(section["input_geometry_records"]),sectionInputs=L(section["section_input_records"]),outputs=L(section["section_output_records"]);
        var source0=D(inputs[0]);var source1=D(inputs[1]);string id=(string)source1["source_jid"];
        var near=D(Clone(Geometry(source0)));foreach(string key in new string[]{"start","end"}){IList v=L(near[key]);v[2]=Convert.ToDouble(v[2])+delta;}
        foreach(object sample in L(near["domain_samples"])){IList v=L(sample);v[2]=Convert.ToDouble(v[2])+delta;}
        SetGeometry(source1,near);SetGeometry(FindSource(sectionInputs,id),near);
        var relation=D(section["section_input_relation"]);SetGeometry(FindSource(L(relation["section_input_records"]),id),near);
        var match=D(section["section_domain_match"]);IList pairs=L(match["matched_geometry_pairs"]);Dictionary<string,object> pair=null;
        foreach(object obj in pairs){var p=D(obj);if(Object.Equals(p["expected_source_jid"],id))pair=p;}
        Need(pair!=null,"near duplicate pair exists");int index=Convert.ToInt32(pair["actual_record_index"]);SetGeometry(D(outputs[index]),near);
        string inputDomain=Domain(inputs),sectionDomain=Domain(sectionInputs),outputDomain=Domain(outputs);
        section["input_geometry_domain_fingerprint"]=inputDomain;section["section_input_domain_fingerprint"]=sectionDomain;
        section["section_output_domain_fingerprint"]=outputDomain;D(section["section_input"])["input_geometry_domain_fingerprint"]=inputDomain;
        relation["section_input_domain_fingerprint"]=sectionDomain;match["input_domain_fingerprint"]=inputDomain;match["output_domain_fingerprint"]=outputDomain;
        return data;
    }
    static void CheckStableTagGuard()
    {
        MethodInfo method=typeof(M2OpenModelAdapter).GetMethod("F18StableSubset",BindingFlags.NonPublic|BindingFlags.Static,null,
            new Type[]{typeof(Dictionary<string,string>),typeof(Dictionary<string,string>),typeof(List<object>).MakeByRefType()},null);
        Need(method!=null,"actual stable-subset guard available");
        var before=new Dictionary<string,string>{{"F:123","[sketch-feature-state]"}};var same=new Dictionary<string,string>{{"F:123","[sketch-feature-state]"}};
        var changedTag=new Dictionary<string,string>{{"F:456","[sketch-feature-state]"}};object[] args={before,same,null};
        Need((bool)method.Invoke(null,args),"stable identical source FeatureTag accepted");args=new object[]{before,changedTag,null};
        Need(!(bool)method.Invoke(null,args) && ((List<object>)args[2]).Count==1,"changed source FeatureTag rejected by actual combined guard");
    }
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs e){
            string file=new AssemblyName(e.Name).Name+".dll";
            foreach(string directory in new string[]{Environment.GetEnvironmentVariable("F21_COMPARE_BUILD"),@"C:\0sf\ug\NXBIN\managed"})
                if(!String.IsNullOrEmpty(directory)){string path=Path.Combine(directory,file);if(File.Exists(path))return Assembly.LoadFrom(path);}
            return null;};
        return Run(args);
    }
    static int Run(string[] args)
    {
        try{
            if(args.Length!=1)throw new ArgumentException("raw fixture path required");
            Console.WriteLine("Comparer assembly: "+typeof(F21SweepDefinitionComparison).Assembly.Location);
            Console.WriteLine("Bridge assembly: "+typeof(M2Json).Assembly.Location);
            var root=D(json.DeserializeObject(File.ReadAllText(args[0])));var before=D(root["before"]);var after=D(root["after"]);
            string originalBefore=M2Json.CanonicalText(before),originalAfter=M2Json.CanonicalText(after);Dictionary<string,object> evidence;
            Need(Compare(before,after,out evidence) && Object.Equals(evidence["status"],"VERIFIED"),"real before/after comparator pass");
            var mappings=L(evidence["output_jid_mapping"]);Need(mappings.Count==3,"three real output mappings");
            Need(mappings.Cast<object>().All(x=>!Object.Equals(D(x)["before_output_jid"],D(x)["after_output_jid"])),"three real output IDs changed");
            foreach(object item in mappings){var m=D(item);string source=(string)m["source_jid"];
                var bp=L(D(Section(before)["section_domain_match"])["matched_geometry_pairs"]).Cast<object>().Select(D).Single(x=>Object.Equals(x["expected_source_jid"],source));
                var ap=L(D(Section(after)["section_domain_match"])["matched_geometry_pairs"]).Cast<object>().Select(D).Single(x=>Object.Equals(x["expected_source_jid"],source));
                Need(Object.Equals(m["before_output_jid"],bp["section_output_jid"])&&Object.Equals(m["after_output_jid"],ap["section_output_jid"])&&
                     Object.Equals(m["before_record_index"],bp["actual_record_index"])&&Object.Equals(m["after_record_index"],ap["actual_record_index"]),"real mapping preserves source/index/output relation");}
            Need(M2Json.CanonicalText(before)==originalBefore && M2Json.CanonicalText(after)==originalAfter,"compare did not mutate either input");
            Reject("source feature id changed",before,after,d=>Feature(d)["sketch_feature_id"]="other-sketch");
            Reject("swept feature id changed",before,after,d=>Feature(d)["feature_id"]="other-feature");
            Reject("swept feature tag changed",before,after,d=>Feature(d)["feature_tag"]="other-tag");
            Reject("body tag changed",before,after,d=>Feature(d)["body_tag"]="other-body");
            Reject("native orientation changed",before,after,d=>Feature(d)["orientation_mode"]="linear_twist");
            Reject("native tolerance changed",before,after,d=>Feature(d)["g0_tolerance"]=0.1);
            Reject("guide membership changed",before,after,d=>L(Feature(d)["path_curve_ids"])[0]="different-path");
            Reject("guide directed endpoint changed",before,after,d=>L(L(D(Feature(d)["guide_evidence"])["element_end_points_mm"])[0])[2]=6.0);
            Reject("section source rule changed",before,after,d=>D(Section(d)["section_input"])["rule_kind"]="CurveDumbRule");
            Reject("section source status missing",before,after,d=>Section(d).Remove("status"));
            Reject("section status mismatch",before,after,d=>Section(d)["status"]="MISMATCH");
            Reject("output count changed",before,after,d=>Section(d)["section_output_count"]=4);
            Reject("output line geometry changed",before,after,d=>L(Geometry(Record(Section(d),"section_output_records",0))["domain_samples"])[2]=new List<object>{0,9,0});
            Reject("output fingerprint forged",before,after,d=>Record(Section(d),"section_output_records",0)["geometry_fingerprint"]="forged");
            Reject("pair index changed",before,after,d=>Pair(Section(d),0)["actual_record_index"]=99);
            Reject("pair output ID inconsistent",before,after,d=>Pair(Section(d),0)["section_output_jid"]="not-output-id");
            Reject("duplicate output ID",before,after,d=>Record(Section(d),"section_output_records",1)["section_output_jid"]=Record(Section(d),"section_output_records",0)["section_output_jid"]);
            Reject("duplicate source ID",before,after,d=>Record(Section(d),"input_geometry_records",1)["source_jid"]=Record(Section(d),"input_geometry_records",0)["source_jid"]);
            Reject("missing match proof",before,after,d=>D(Section(d)["section_domain_match"]).Remove("matched_geometry_pairs"));
            Reject("unsupported reference count",before,after,d=>Section(d)["excluded_reference_curve_count"]=1);
            var near=NearDuplicate(after,0.0000005);bool nearResult=Compare(near,near,out evidence);
            Need(!nearResult && Convert.ToString(evidence["reason"]).Contains("exactly one"),"self-consistent 0.5e-6 mm near-duplicate rejected as ambiguous");
            var outside=NearDuplicate(after,0.000002);Need(Compare(outside,outside,out evidence),"self-consistent 2e-6 mm separation is not treated as near-duplicate");
            foreach(double bad in new double[]{Double.NaN,Double.PositiveInfinity}){
                var changed=D(Clone(after));L(Geometry(Record(Section(changed),"section_output_records",0))["start"])[0]=bad;bool classified=false;
                try{Compare(before,changed,out evidence);}catch(InvalidOperationException){classified=true;}catch(F21SweepDefinitionComparison.UnsupportedDefinitionException){classified=false;}
                Need(classified,"nonfinite must remain InvalidOperationException");}
            CheckStableTagGuard();
            Console.WriteLine("F21 real-definition comparison checks passed: "+checks);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
    }
}
