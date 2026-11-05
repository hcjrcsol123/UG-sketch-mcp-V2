using System;
using System.IO;
using System.Reflection;
using NXOpen;

/// <summary>NX Journal loader for the independently compiled F09 builder variants.</summary>
public class NXJournal
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";

    public static void Main(string[] args)
    {
        string evidence = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        Directory.CreateDirectory(evidence);
        string marker = Path.Combine(evidence, "F09-pattern-variants-journal-" + Guid.NewGuid().ToString("N") + ".json");
        WriteMarker(marker, "started", false, null);
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            string assemblyPath = Path.Combine(evidence, "F09PatternVariants.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Build tools/v2/build-f09-pattern-variants.ps1 before running this Journal.", assemblyPath);
            Assembly variants = Assembly.LoadFrom(assemblyPath);
            Type entry = variants.GetType("F09PatternVariants", true, false);
            MethodInfo run = entry.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (run == null) throw new MissingMethodException("F09PatternVariants.Run was not found.");
            WriteMarker(marker, "invoking", true, assemblyPath);
            run.Invoke(null, null);
            WriteMarker(marker, "completed", true, assemblyPath);
        }
        catch (Exception ex)
        {
            Exception actual = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            WriteMarker(marker, "failed", false, actual.ToString());
            throw;
        }
    }

    private static void WriteMarker(string path, string state, bool invoked, string detail)
    {
        string safe = detail == null ? "null" : "\"" + Escape(detail) + "\"";
        string json = "{\"entry_class\":\"NXJournal\",\"state\":\"" + Escape(state) +
            "\",\"variants_invoked\":" + (invoked ? "true" : "false") +
            ",\"detail\":" + safe + ",\"updated_utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
    }

    private static string Escape(string value)
    {
        if (value == null) return "";
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name;
        try { name = new AssemblyName(args.Name).Name + ".dll"; }
        catch { return null; }
        string evidence = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        string managed = @"C:\0sf\ug\NXBIN\managed";
        string[] roots = new string[] { evidence, managed };
        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }

    public static int Run()
    {
        Main(new string[0]);
        return 0;
    }

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Explicitly;
    }
}
