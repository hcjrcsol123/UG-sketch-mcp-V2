// NX2506 exploratory test for nested pattern reader Undo boundaries.
// The wrapper supplies a disposable copy as the sole work part.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using NXOpen;
using NXOpen.Features;

public static class NXJournal
{
    private static string N(double x) { return x.ToString("R", CultureInfo.InvariantCulture); }
    private static string P(Point3d p) { return N(p.X)+","+N(p.Y)+","+N(p.Z); }
    private static string V(Vector3d p) { return N(p.X)+","+N(p.Y)+","+N(p.Z); }
    private static string Inventory(Part p)
    {
        List<string> rows = new List<string>();
        foreach (Expression e in p.Expressions) rows.Add("E|"+e.Tag+"|"+e.JournalIdentifier+"|"+e.Name+"|"+e.RightHandSide+"|"+N(e.Value)+"|"+(e.Units==null?"":e.Units.JournalIdentifier));
        foreach (NXOpen.Point x in p.Points.ToArray()) rows.Add("P|"+x.Tag+"|"+x.JournalIdentifier+"|"+P(x.Coordinates));
        foreach (Direction x in p.Directions.ToArray()) rows.Add("D|"+x.Tag+"|"+x.JournalIdentifier+"|"+P(x.Origin)+"|"+V(x.Vector));
        foreach (Axis x in p.Axes.ToArray()) rows.Add("A|"+x.Tag+"|"+x.JournalIdentifier+"|"+(x.Point==null?"":x.Point.Tag.ToString())+"|"+(x.Direction==null?"":x.Direction.Tag.ToString()));
        foreach (Feature f in p.Features)
        {
            List<string> parents = new List<string>(); foreach (Feature x in f.GetParents()) parents.Add(x.Tag.ToString()); parents.Sort(StringComparer.Ordinal);
            rows.Add("F|"+f.Tag+"|"+f.JournalIdentifier+"|"+f.FeatureType+"|"+String.Join(",", parents.ToArray()));
        }
        foreach (Body b in p.Bodies)
        {
            rows.Add("B|"+b.Tag+"|"+b.JournalIdentifier+"|"+b.IsSolidBody);
            foreach (Edge e in b.GetEdges())
            {
                Point3d a,z; e.GetVertices(out a,out z); List<string> faces = new List<string>();
                foreach (Face f in e.GetFaces()) faces.Add(f.Tag.ToString()); faces.Sort(StringComparer.Ordinal);
                rows.Add("G|"+b.Tag+"|"+e.Tag+"|"+e.SolidEdgeType+"|"+P(a)+"|"+P(z)+"|"+String.Join(",",faces.ToArray()));
            }
        }
        rows.Sort(StringComparer.Ordinal); return String.Join("\n",rows.ToArray());
    }
    private static Feature Find(Part p,string id)
    { foreach(Feature f in p.Features) if(f.JournalIdentifier==id)return f; throw new InvalidOperationException("missing feature "+id); }
    public static int Run()
    {
        string path=Environment.GetEnvironmentVariable("F18_READER_REPORT");
        Dictionary<string,object> report=new Dictionary<string,object>();
        Session s=Session.GetSession(); Part p=s.Parts.Work;
        try
        {
            if(p==null || p.IsModified)throw new InvalidOperationException("a clean disposable probe part is required");
            string baseline=Inventory(p); report["baseline_inventory"]=baseline; report["baseline_dirty"]=p.IsModified;
            Session.UndoMarkId outer=s.SetUndoMark(Session.MarkVisibility.Invisible,"F18 outer edit");
            Extrude seed=(Extrude)Find(p,"EXTRUDE(0)"); ExtrudeBuilder eb=null;
            try { eb=p.Features.CreateExtrudeBuilder(seed); eb.Limits.EndExtend.Value.RightHandSide="5"; eb.CommitFeature(); }
            finally {if(eb!=null)eb.Destroy();}
            int outerUpdate=s.UpdateManager.DoUpdate(outer); report["edit_update_errors"]=outerUpdate;
            if(outerUpdate!=0 || !p.IsModified)throw new InvalidOperationException("outer edit failed to create a dirty state");
            string edited=Inventory(p); report["edited_inventory"]=edited; report["edited_dirty"]=p.IsModified;
            PatternFeature pattern=(PatternFeature)Find(p,"Pattern Feature(1)");
            Dictionary<string,object> data=V2FeaturePatternFactory.ReadIsolated(s,p,pattern,delegate{return Inventory(p);});
            string afterReader=Inventory(p); report["after_reader_inventory"]=afterReader; report["after_reader_dirty"]=p.IsModified;
            report["reader_data"]=data; report["inner_preserved_edit"]=String.Equals(edited,afterReader,StringComparison.Ordinal) && p.IsModified;
            // Resolve after the reader's Undo rather than relying on stale handles.
            report["resolved_seed"]=Find(p,"EXTRUDE(0)").FeatureType;
            report["resolved_pattern"]=Find(p,"Pattern Feature(1)").FeatureType;
            s.UndoToMarkWithStatus(outer,"F18 outer edit");
            int undoUpdate=s.UpdateManager.DoUpdate(outer); report["outer_undo_update_errors"]=undoUpdate;
            s.DeleteUndoMark(outer,"F18 outer edit");
            string restored=Inventory(p); report["restored_inventory"]=restored; report["restored_dirty"]=p.IsModified;
            report["outer_restored"]=undoUpdate==0 && !p.IsModified && String.Equals(baseline,restored,StringComparison.Ordinal);
            report["passed"]=Object.Equals(report["inner_preserved_edit"],true) && Object.Equals(report["outer_restored"],true);
        }
        catch(Exception ex) {report["passed"]=false; report["error"]=ex.ToString();}
        File.WriteAllText(path,new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue}.Serialize(report),new UTF8Encoding(false));
        return Object.Equals(report["passed"],true)?0:1;
    }
}
