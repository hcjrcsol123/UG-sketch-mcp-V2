// NX journal wrapper for F10EdgeAcceptance.dll. It uses only the already-open
// NX session and never starts, restarts, closes, or terminates NX.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NXOpen;

public class NXJournal
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";

    public static void Main(string[] args)
    {
        string workspaceRoot = Path.Combine(ProjectRoot, "workspace", "v2");
        string evidenceDirectory = Path.Combine(workspaceRoot, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        string runId = Guid.NewGuid().ToString("N");
        string markerPath = Path.Combine(evidenceDirectory, "F10-edge-journal-start-" + runId + ".json");
        WriteMarker(markerPath, "started", null);
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            string edgeAcceptancePath = Path.Combine(evidenceDirectory, "F10EdgeAcceptance.dll");
            string adapterPath = Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Entry.dll");
            if (!File.Exists(edgeAcceptancePath))
                throw new FileNotFoundException("Build tests/nx/v2/build-f10-edge-acceptance.ps1 before running this Journal.", edgeAcceptancePath);
            if (!File.Exists(adapterPath))
                throw new FileNotFoundException("Build the production M2 assemblies before running this Journal.", adapterPath);

            Assembly adapterAssembly = Assembly.LoadFrom(adapterPath);
            Type adapterType = adapterAssembly.GetType("M2OpenModelAdapter", true, false);
            object adapter = Activator.CreateInstance(adapterType);
            Assembly acceptance = Assembly.LoadFrom(edgeAcceptancePath);
            Type entry = acceptance.GetType("F10EdgeAcceptance", true, false);
            MethodInfo run = entry.GetMethod("Run", BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(Session), adapterType, typeof(string) }, null);
            if (run == null) throw new MissingMethodException("F10EdgeAcceptance.Run(Session, M2OpenModelAdapter, string) was not found.");

            Session session = Session.GetSession();
            if (session == null) throw new InvalidOperationException("NX Session.GetSession returned null.");
            WriteMarker(markerPath, "invoking", edgeAcceptancePath);
            object raw = run.Invoke(null, new object[] { session, adapter, workspaceRoot });
            IDictionary result = raw as IDictionary;
            if (result == null || !Object.Equals(result["passed"], true))
                throw new InvalidOperationException("F10EdgeAcceptance returned no passing report. Inspect the unique F10-edge-*.json evidence report.");
            WriteMarker(markerPath, "passed", edgeAcceptancePath);
        }
        catch (Exception ex)
        {
            Exception failure = ex is TargetInvocationException && ((TargetInvocationException)ex).InnerException != null
                ? ((TargetInvocationException)ex).InnerException : ex;
            WriteMarker(markerPath, "failed", null, failure.ToString());
            throw;
        }
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

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string simpleName = new AssemblyName(args.Name).Name + ".dll";
        string[] roots = new string[]
        {
            Path.Combine(ProjectRoot, "workspace", "build", "current", "M2"),
            @"C:\0sf\ug\NXBIN\managed"
        };
        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, simpleName);
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }

    private static void WriteMarker(string path, string status, string assemblyPath)
    {
        WriteMarker(path, status, assemblyPath, null);
    }

    private static void WriteMarker(string path, string status, string assemblyPath, string error)
    {
        string json = "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Run\",\"status\":\"" + Escape(status) +
            "\",\"acceptance_assembly\":" + (assemblyPath == null ? "null" : "\"" + Escape(assemblyPath) + "\"") +
            ",\"error\":" + (error == null ? "null" : "\"" + Escape(error) + "\"") +
            ",\"updated_utc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"nx_started_by_wrapper\":false,\"nx_closed_by_wrapper\":false}";
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
    }

    private static string Escape(string value)
    {
        if (value == null) return String.Empty;
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
