using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using NxSketchMcp.M2;

// Read-only projection of supported native Swept definitions. Only the two
// transient Section output identifiers are excluded from exact comparison.
public static class F21SweepDefinitionComparison
{
    public sealed class UnsupportedDefinitionException : Exception
    {
        public UnsupportedDefinitionException(string message) : base(message) { }
    }
    private const double Tol = 1.0e-6;
    private sealed class Projection
    {
        internal Dictionary<string,object> Value;
        internal Dictionary<string,Dictionary<string,object>> Mappings;
    }
    private static UnsupportedDefinitionException Invalid(string reason)
    { return new UnsupportedDefinitionException("Unsupported sweep definition: "+reason); }
    private static Dictionary<string,object> Map(object value,string field)
    { Dictionary<string,object> d=value as Dictionary<string,object>;if(d==null)throw Invalid(field+" must be an object");return d; }
    private static object Field(Dictionary<string,object> value,string name)
    { object v;if(value==null || !value.TryGetValue(name,out v))throw Invalid("missing "+name);return v; }
    private static string Text(object value,string field)
    { string s=value as string;if(String.IsNullOrWhiteSpace(s))throw Invalid(field+" must be a nonempty string");return s; }
    private static IList List(object value,string field)
    { IList l=value as IList;if(l==null)throw Invalid(field+" must be a list");return l; }
    private static int Integer(object value,string field)
    {
        if(value is int)return (int)value;
        if(value is long && (long)value>=Int32.MinValue && (long)value<=Int32.MaxValue)return (int)(long)value;
        throw Invalid(field+" must be an integer");
    }
    private static double Number(object value,string field)
    {
        if(!(value is int) && !(value is long) && !(value is float) && !(value is double) && !(value is decimal))
            throw Invalid(field+" must be a finite number");
        double n=Convert.ToDouble(value,CultureInfo.InvariantCulture);
        if(Double.IsNaN(n)||Double.IsInfinity(n))throw new InvalidOperationException(field+" contains a non-finite number");
        return n;
    }
    private static double[] Vector(object value,string field)
    {
        IList l=List(value,field);if(l.Count!=3)throw Invalid(field+" must have three coordinates");
        return new double[]{Number(l[0],field),Number(l[1],field),Number(l[2],field)};
    }
    private static double Distance(double[] a,double[] b)
    { double x=a[0]-b[0],y=a[1]-b[1],z=a[2]-b[2];double d=Math.Sqrt(x*x+y*y+z*z);if(Double.IsInfinity(d)||Double.IsNaN(d))throw new InvalidOperationException("geometry arithmetic is non-finite");return d; }
    private static object Clone(object value)
    {
        Dictionary<string,object> d=value as Dictionary<string,object>;
        if(d!=null){Dictionary<string,object> copy=new Dictionary<string,object>(StringComparer.Ordinal);foreach(KeyValuePair<string,object> entry in d)copy.Add(entry.Key,Clone(entry.Value));return copy;}
        IList l=value as IList;if(l!=null){List<object> copy=new List<object>();foreach(object item in l)copy.Add(Clone(item));return copy;}
        return value;
    }
    private static void Equal(string actual,string expected,string field)
    {if(!String.Equals(actual,expected,StringComparison.Ordinal))throw Invalid(field+" differs");}
    private static void Status(Dictionary<string,object> d,string key,string expected)
    {Equal(Text(Field(d,key),key),expected,key);}
    private static List<Dictionary<string,object>> Records(object value,string field)
    {
        IList items=List(value,field);List<Dictionary<string,object>> result=new List<Dictionary<string,object>>();
        foreach(object item in items)result.Add(Map(item,field+" entry"));return result;
    }
    private static string Domain(List<Dictionary<string,object>> records)
    {return V2SketchProfileAnalyzer.F12DomainFingerprint(records);}
    private static void ValidateLine(Dictionary<string,object> record)
    {
        Equal(Text(Field(record,"curve_type"),"curve_type"),"Line","curve_type");
        Dictionary<string,object> geometry=Map(Field(record,"geometry"),"geometry");
        string[] required={"kind","start","end","length_mm","parameter_start","parameter_end","domain_samples","is_closed_domain"};
        if(geometry.Count!=required.Length)throw Invalid("LINE geometry fields are incomplete or unsupported");
        foreach(string key in required)Field(geometry,key);
        Equal(Text(geometry["kind"],"kind"),"LINE","geometry kind");
        if(!(geometry["is_closed_domain"] is bool) || (bool)geometry["is_closed_domain"])throw Invalid("LINE must have open domain");
        double[] start=Vector(geometry["start"],"start"),end=Vector(geometry["end"],"end");
        double len=Number(geometry["length_mm"],"length_mm"),distance=Distance(start,end);
        if(len<=Tol || distance<=Tol || Math.Abs(len-distance)>Tol || Number(geometry["parameter_start"],"parameter_start")>=Number(geometry["parameter_end"],"parameter_end"))
            throw Invalid("LINE length or parameter domain is inconsistent");
        IList samples=List(geometry["domain_samples"],"domain_samples");if(samples.Count!=5)throw Invalid("LINE samples incomplete");
        for(int i=0;i<5;i++)
        {
            double t=(double)i/4.0;double[] expected=new double[]{start[0]+t*(end[0]-start[0]),start[1]+t*(end[1]-start[1]),start[2]+t*(end[2]-start[2])};
            if(Distance(Vector(samples[i],"domain sample"),expected)>Tol)throw Invalid("LINE samples inconsistent");
        }
        string fingerprint=Text(Field(record,"geometry_fingerprint"),"geometry_fingerprint");
        List<Dictionary<string,object>> computed=new List<Dictionary<string,object>>{new Dictionary<string,object>{{"geometry",geometry}}};
        List<Dictionary<string,object>> supplied=new List<Dictionary<string,object>>{new Dictionary<string,object>{{"geometry_fingerprint",fingerprint}}};
        Equal(Domain(computed),Domain(supplied),"geometry fingerprint");
    }
    private static HashSet<string> StringSet(object value,string field)
    {
        IList l=List(value,field);HashSet<string> result=new HashSet<string>(StringComparer.Ordinal);
        foreach(object item in l)if(!result.Add(Text(item,field)))throw Invalid(field+" contains duplicates");return result;
    }
    private static bool Equivalent(Dictionary<string,object> a,Dictionary<string,object> b)
    {Dictionary<string,object> evidence;return V2SketchProfileAnalyzer.F12GeometryRecordsEquivalent(a,b,Tol,out evidence);}
    private static void RequireEmpty(Dictionary<string,object> d,string key)
    {if(List(Field(d,key),key).Count!=0)throw Invalid(key+" must be empty");}

    private static Dictionary<string,object> ValidateSection(Dictionary<string,object> feature)
    {
        Dictionary<string,object> section=Map(Field(feature,"section_evidence"),"section_evidence");Status(section,"status","VERIFIED");
        List<Dictionary<string,object>> inputs=Records(Field(section,"input_geometry_records"),"input_geometry_records");
        List<Dictionary<string,object>> sectionInputs=Records(Field(section,"section_input_records"),"section_input_records");
        List<Dictionary<string,object>> outputs=Records(Field(section,"section_output_records"),"section_output_records");
        int n=inputs.Count;
        int excluded=Integer(Field(section,"excluded_reference_curve_count"),"excluded_reference_curve_count");
        if(n<1 || outputs.Count!=n || sectionInputs.Count!=n || Integer(Field(section,"input_curve_count"),"input_curve_count")!=n ||
           excluded<0 || List(Field(section,"excluded_reference_records"),"excluded_reference_records").Count!=excluded ||
           (long)Integer(Field(section,"source_curve_count"),"source_curve_count")!=(long)n+excluded ||
           Integer(Field(section,"section_output_count"),"section_output_count")!=n)
            throw Invalid("source and output counts differ");
        Dictionary<string,Dictionary<string,object>> sources=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
        foreach(Dictionary<string,object> input in inputs)
        {
            ValidateLine(input);string id=Text(Field(input,"source_jid"),"source_jid");
            Status(input,"uf_reference_status","Active");
            if(sources.ContainsKey(id))throw Invalid("source identifiers are not unique");sources.Add(id,input);
        }
        HashSet<string> sectionSourceIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(Dictionary<string,object> input in sectionInputs)
        {
            ValidateLine(input);string id=Text(Field(input,"source_jid"),"source_jid");Dictionary<string,object> origin;
            if(!sectionSourceIds.Add(id) || !sources.TryGetValue(id,out origin) ||
                M2Json.CanonicalText(Field(origin,"geometry"))!=M2Json.CanonicalText(Field(input,"geometry")))
                throw Invalid("section input does not agree with source geometry");
        }
        Dictionary<string,object> inputRelation=Map(Field(section,"section_input"),"section_input");
        Status(inputRelation,"feature_rule_readback_status","VERIFIED");Status(inputRelation,"relation_readback_status","MATCHED_SOURCE_CURVES");
        Status(inputRelation,"rule_kind","CurveFeatureRule");
        Equal(Text(Field(inputRelation,"source_feature_jid"),"source_feature_jid"),Text(Field(feature,"sketch_feature_id"),"sketch_feature_id"),"sketch feature");
        if(!StringSet(Field(inputRelation,"source_geometry_jids"),"source_geometry_jids").SetEquals(sources.Keys))throw Invalid("section source identifiers differ");
        Dictionary<string,object> relation=Map(Field(section,"section_input_relation"),"section_input_relation");
        Status(relation,"status","MATCHED_SOURCE_CURVES");Status(relation,"whole_sketch_feature_rule_status","VERIFIED");
        if(!Object.Equals(Field(relation,"source_jid_set_match"),true) || !Object.Equals(Field(relation,"whole_sketch_feature_rule_matches_sketch"),true) ||
           !Object.Equals(Field(relation,"whole_sketch_feature_rule_observed"),true))throw Invalid("whole-sketch source relation is not verified");
        if(M2Json.CanonicalText(Field(relation,"section_input_records"))!=M2Json.CanonicalText(Field(section,"section_input_records")))
            throw Invalid("section input relation records differ");
        Dictionary<string,object> inputMatch=Map(Field(relation,"geometry_domain_match"),"input geometry domain match");
        Status(inputMatch,"status","MATCHED");RequireEmpty(inputMatch,"unmatched_expected_records");RequireEmpty(inputMatch,"unmatched_actual_records");
        if(Integer(Field(inputMatch,"expected_record_count"),"input expected count")!=n || Integer(Field(inputMatch,"actual_record_count"),"input actual count")!=n)
            throw Invalid("section input match counts differ");
        List<Dictionary<string,object>> inputPairs=Records(Field(inputMatch,"matched_geometry_pairs"),"input matched pairs");
        if(inputPairs.Count!=n)throw Invalid("section input pair count differs");
        HashSet<string> seenInputSources=new HashSet<string>(StringComparer.Ordinal);HashSet<int> seenInputIndices=new HashSet<int>();
        foreach(Dictionary<string,object> pair in inputPairs)
        {
            string id=Text(Field(pair,"expected_source_jid"),"input pair source");Dictionary<string,object> source;
            int index=Integer(Field(pair,"actual_record_index"),"input pair index");
            if(!sources.TryGetValue(id,out source) || !seenInputSources.Add(id) || index<0 || index>=n || !seenInputIndices.Add(index) ||
               !Object.Equals(Field(pair,"actual_source_jid"),id) || !Object.Equals(Field(sectionInputs[index],"source_jid"),id) ||
               !Equivalent(source,sectionInputs[index]))throw Invalid("section input pair identity or geometry differs");
        }
        HashSet<string> outputIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(Dictionary<string,object> output in outputs)
        {ValidateLine(output);if(!outputIds.Add(Text(Field(output,"section_output_jid"),"section_output_jid")))throw Invalid("output identifiers are not unique");}
        string inputDomain=Domain(inputs),outputDomain=Domain(outputs);
        Equal(Text(Field(section,"input_geometry_domain_fingerprint"),"input domain"),inputDomain,"input domain fingerprint");
        Equal(Text(Field(section,"section_output_domain_fingerprint"),"output domain"),outputDomain,"output domain fingerprint");
        Equal(Text(Field(section,"section_input_domain_fingerprint"),"section input domain"),Domain(sectionInputs),"section input fingerprint");
        Equal(Text(Field(relation,"section_input_domain_fingerprint"),"relation input domain"),Domain(sectionInputs),"relation input fingerprint");
        Equal(Text(Field(inputRelation,"input_geometry_domain_fingerprint"),"input relation domain"),inputDomain,"input relation fingerprint");
        Dictionary<string,object> match=Map(Field(section,"section_domain_match"),"section_domain_match");Status(match,"status","MATCHED");
        if(Integer(Field(match,"expected_record_count"),"expected_record_count")!=n || Integer(Field(match,"actual_record_count"),"actual_record_count")!=n)
            throw Invalid("matched record counts differ");
        RequireEmpty(match,"unmatched_expected_records");RequireEmpty(match,"unmatched_actual_records");
        Equal(Text(Field(match,"input_domain_fingerprint"),"match input fingerprint"),inputDomain,"match input fingerprint");
        Equal(Text(Field(match,"output_domain_fingerprint"),"match output fingerprint"),outputDomain,"match output fingerprint");
        List<Dictionary<string,object>> pairs=Records(Field(match,"matched_geometry_pairs"),"matched_geometry_pairs");
        if(pairs.Count!=n)throw Invalid("matched pair count differs");
        Dictionary<string,object> mapping=new Dictionary<string,object>(StringComparer.Ordinal);
        HashSet<int> seenIndices=new HashSet<int>();
        foreach(Dictionary<string,object> pair in pairs)
        {
            string id=Text(Field(pair,"expected_source_jid"),"expected_source_jid");Dictionary<string,object> source;
            int index=Integer(Field(pair,"actual_record_index"),"actual_record_index");
            if(!sources.TryGetValue(id,out source) || mapping.ContainsKey(id) || index<0 || index>=n || !seenIndices.Add(index))throw Invalid("pair identity or index is invalid");
            Dictionary<string,object> output=outputs[index];string outputId=Text(Field(output,"section_output_jid"),"section_output_jid");
            Equal(Text(Field(pair,"section_output_jid"),"pair output identifier"),outputId,"pair output identifier");
            if(Field(pair,"actual_source_jid")!=null || !Equivalent(source,output))throw Invalid("pair geometry or source identity does not match");
            Status(pair,"geometry_match","UNORDERED_ENDPOINTS");Dictionary<string,object> matchEvidence=Map(Field(pair,"geometry_match_evidence"),"geometry_match_evidence");
            Status(matchEvidence,"kind","LINE");Status(matchEvidence,"status","EQUIVALENT");Status(matchEvidence,"match_mode","UNORDERED_ENDPOINTS");
            if(Number(Field(matchEvidence,"tolerance_mm"),"tolerance_mm")!=Tol)throw Invalid("pair tolerance differs");
            mapping.Add(id,new Dictionary<string,object>{{"section_output_jid",outputId},{"actual_record_index",index},{"geometry",Field(output,"geometry")}});
        }
        foreach(KeyValuePair<string,Dictionary<string,object>> source in sources)
        {
            int count=0;foreach(Dictionary<string,object> output in outputs)if(Equivalent(source.Value,output))count++;
            if(count!=1)throw Invalid("source does not have exactly one output match");
        }
        foreach(Dictionary<string,object> output in outputs)
        {
            int count=0;foreach(KeyValuePair<string,Dictionary<string,object>> source in sources)if(Equivalent(source.Value,output))count++;
            if(count!=1)throw Invalid("output does not have exactly one source match");
        }
        return mapping;
    }
    private static Projection Build(Dictionary<string,object> definitions)
    {
        if(definitions==null)throw Invalid("definitions are null");
        Dictionary<string,object> normalized=Map(Clone(definitions),"normalized definitions");
        Dictionary<string,Dictionary<string,object>> mappings=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
        foreach(KeyValuePair<string,object> entry in definitions)
        {
            Text(entry.Key,"feature id");Dictionary<string,object> feature=Map(entry.Value,"feature definition");
            Equal(Text(Field(feature,"feature_id"),"feature_id"),entry.Key,"feature identity");
            mappings.Add(entry.Key,ValidateSection(feature));
            Dictionary<string,object> projectedFeature=Map(normalized[entry.Key],"projected feature");
            Dictionary<string,object> section=Map(Field(projectedFeature,"section_evidence"),"projected section");
            foreach(Dictionary<string,object> output in Records(Field(section,"section_output_records"),"output records"))output.Remove("section_output_jid");
            Dictionary<string,object> match=Map(Field(section,"section_domain_match"),"section_domain_match");
            foreach(Dictionary<string,object> pair in Records(Field(match,"matched_geometry_pairs"),"matched pairs"))pair.Remove("section_output_jid");
        }
        return new Projection{Value=normalized,Mappings=mappings};
    }
    public static Dictionary<string,object> Normalize(Dictionary<string,object> definitions)
    {return Build(definitions).Value;}
    public static bool Compare(Dictionary<string,object> before,Dictionary<string,object> after,out Dictionary<string,object> evidence)
    {
        try
        {
            Projection a=Build(before),b=Build(after);List<object> mappings=new List<object>();
            if(!new HashSet<string>(a.Mappings.Keys,StringComparer.Ordinal).SetEquals(b.Mappings.Keys))throw Invalid("feature identifier sets differ");
            foreach(string feature in a.Mappings.Keys)
            {
                Dictionary<string,object> x=a.Mappings[feature],y=b.Mappings[feature];
                if(!new HashSet<string>(x.Keys,StringComparer.Ordinal).SetEquals(y.Keys))throw Invalid("source identifier sets differ");
                foreach(string source in x.Keys)
                {
                    Dictionary<string,object> oldValue=Map(x[source],"old mapping"),newValue=Map(y[source],"new mapping");
                    if(M2Json.CanonicalText(oldValue["geometry"])!=M2Json.CanonicalText(newValue["geometry"]))throw Invalid("mapped output geometry changed");
                    mappings.Add(new Dictionary<string,object>{{"feature_id",feature},{"source_jid",source},
                        {"before_output_jid",oldValue["section_output_jid"]},{"after_output_jid",newValue["section_output_jid"]},
                        {"before_record_index",oldValue["actual_record_index"]},{"after_record_index",newValue["actual_record_index"]}});
                }
            }
            bool equal=String.Equals(M2Json.CanonicalText(a.Value),M2Json.CanonicalText(b.Value),StringComparison.Ordinal);
            evidence=new Dictionary<string,object>{{"status",equal?"VERIFIED":"MISMATCH"},{"output_jid_mapping",mappings},
                {"comparison","exact projection excluding only validated Section output identifiers"}};
            return equal;
        }
        catch(UnsupportedDefinitionException ex)
        {
            evidence=new Dictionary<string,object>{{"status","MISMATCH"},{"reason",ex.Message},{"error_type",ex.GetType().FullName}};
            return false;
        }
    }
    public static bool CompareDefinition(Dictionary<string,object> before,Dictionary<string,object> after,out Dictionary<string,object> evidence)
    {
        try
        {
            string id=Text(Field(before,"feature_id"),"feature_id");
            return Compare(new Dictionary<string,object>{{id,before}},new Dictionary<string,object>{{id,after}},out evidence);
        }
        catch(UnsupportedDefinitionException ex){evidence=new Dictionary<string,object>{{"status","MISMATCH"},{"reason",ex.Message},{"error_type",ex.GetType().FullName}};return false;}
    }
}
