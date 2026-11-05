using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
class F21InventorySyntheticHarness {
 static int Main(){var j=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};string line;while((line=Console.ReadLine())!=null){try{var r=(Dictionary<string,object>)j.DeserializeObject(line);string mode=(string)r["mode"];
 if(mode=="compare"){var x=F21InventoryComparer.Compare((string)r["before"],(string)r["after"],(Dictionary<string,object>)r["maps_before"],(Dictionary<string,object>)r["maps_after"]);Console.WriteLine(j.Serialize(x));}
 else if(mode=="nonfinite"){var m=(Dictionary<string,object>)r["mapping"];var samples=(object[])m["samples"];var s=(Dictionary<string,object>)samples[0];var p=(object[])s["point_mm"];p[0]=(string)r["value"]=="nan"?Double.NaN:Double.PositiveInfinity;try{F21InventoryComparer.ValidateMap(m,(string)m["face_tag"],m["uv_limits"],r["normal"],r["origin"]);Console.WriteLine("{\"passed\":true}");}catch(Exception){Console.WriteLine("{\"passed\":false}");}}
 else if(mode=="steps"){double a=Double.Parse((string)r["a"],System.Globalization.CultureInfo.InvariantCulture),b=Double.Parse((string)r["b"],System.Globalization.CultureInfo.InvariantCulture);Console.WriteLine(j.Serialize(new Dictionary<string,object>{{"steps",F21InventoryComparer.Steps(a,b)}}));}
 else throw new Exception("unknown synthetic mode");}catch(Exception e){Console.WriteLine(j.Serialize(new Dictionary<string,object>{{"passed",false},{"error",e.GetType().Name}}));}}return 0;}
}
