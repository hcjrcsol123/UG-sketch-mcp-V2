// Discovery in the root-operated, idle disposable F11 GUI model only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;
public static class F11GuiSolverProbe
{
    public static void Main(string[] args)
    {
        string root = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
        string output = Path.Combine(root, "workspace", "v2", "evidence", "F11-gui-solver-probe-" + Guid.NewGuid().ToString("N") + ".json");
        Dictionary<string, object> report = new Dictionary<string, object>();
        List<object> rows = new List<object>();
        report["scope"] = "GUI API discovery, not product acceptance";
        report["pid"] = Process.GetCurrentProcess().Id;
        report["sketches"] = rows;
        Session session = Session.GetSession();
        Part part = session.Parts.Work;
        report["application"] = session.ApplicationName;
        report["task_environment"] = session.IsInTaskEnvironment;
        try
        {
            if (part == null || !part.FullPath.StartsWith(Path.Combine(root, "workspace", "v2", "models", "F11-"), StringComparison.OrdinalIgnoreCase) || session.ActiveSketch != null)
                throw new InvalidOperationException("Requires an idle disposable F11 model.");
            report["part_path"] = part.FullPath;
            foreach (Sketch sketch in part.Sketches.ToArray())
            {
                Dictionary<string, object> row = new Dictionary<string, object>();
                rows.Add(row);
                row["before"] = Read(sketch);
                Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 GUI solver discovery");
                try
                {
                    sketch.Activate(Sketch.ViewReorient.False);
                    SketchWorkRegionBuilder region = part.Sketches.CreateWorkRegionBuilder();
                    try { region.Scope = SketchWorkRegionBuilder.ScopeType.EntireSketch; region.Commit(); }
                    finally { region.Destroy(); }
                    sketch.Update();
                    sketch.CalculateStatus();
                    row["after_calculate_while_active"] = Read(sketch);
                    row["task_environment_while_active"] = session.IsInTaskEnvironment;
                    sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                    row["update_errors"] = session.UpdateManager.DoUpdate(mark);
                    row["after_deactivate"] = Read(sketch);
                }
                catch (Exception ex) { row["error"] = ex.ToString(); }
                finally
                {
                    if (session.ActiveSketch != null) sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                    session.UndoToMark(mark, null);
                    session.DeleteUndoMark(mark, null);
                    row["discovery_undo_returned"] = true;
                }
            }
        }
        catch (Exception ex) { report["error"] = ex.ToString(); }
        finally { File.WriteAllText(output, M2Json.CanonicalText(report)); }
    }
    private static Dictionary<string, object> Read(Sketch sketch)
    {
        int dof; Sketch.Status status = sketch.GetStatus(out dof);
        Dictionary<string, object> row = new Dictionary<string, object>();
        row["sketch_id"] = sketch.JournalIdentifier;
        row["status"] = status.ToString(); row["dof"] = dof;
        row["uses_legacy_solver"] = sketch.UsesLegacySolver;
        row["upgrade_upon_activation"] = sketch.UpgradeUponActivation;
        try { UFSket.Status ufStatus; int ufDof; UFSession.GetUFSession().Sket.AskSketchStatus(sketch.Tag, out ufStatus, out ufDof); row["uf_status"] = ufStatus.ToString(); row["uf_dof"] = ufDof; }
        catch (Exception ex) { row["uf_status_error"] = ex.Message; }
        return row;
    }
    public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Immediately; }
}
