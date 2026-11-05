// NX journal loader for F11Acceptance.dll. This wrapper never starts,
// restarts, closes, or terminates NX.
using System;
using System.IO;
using System.Reflection;
using NXOpen;

public class NXJournal
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";

    public static void Main(string[] args)
    {
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        string runId = Guid.NewGuid().ToString("N");
        string markerPath = Path.Combine(evidenceDirectory, "F11-journal-start-" + runId + ".json");
        File.WriteAllText(markerPath,
            "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Main\",\"started_utc\":\"" +
            DateTime.UtcNow.ToString("o") + "\",\"acceptance_invoked\":false}",
            new System.Text.UTF8Encoding(false));
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            string assemblyPath = Path.Combine(evidenceDirectory, "F11Acceptance.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Compile tests/nx/v2/build-f11-acceptance.ps1 before running this Journal.", assemblyPath);
            Assembly acceptance = Assembly.LoadFrom(assemblyPath);
            Type entry = acceptance.GetType("F11Acceptance", true, false);
            MethodInfo main = entry.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
            if (main == null) throw new MissingMethodException("F11Acceptance.Main(string[]) was not found.");
            File.WriteAllText(markerPath,
                "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Main\",\"acceptance_invoked\":true,\"acceptance_assembly\":\"" +
                Escape(assemblyPath) + "\",\"invoke_started_utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}",
                new System.Text.UTF8Encoding(false));
            main.Invoke(null, new object[] { args ?? new string[0] });
            File.WriteAllText(markerPath,
                "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Main\",\"acceptance_invoked\":true,\"invocation_returned\":true,\"acceptance_assembly\":\"" +
                Escape(assemblyPath) + "\",\"completed_utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}",
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            string failurePath = Path.Combine(evidenceDirectory, "F11-acceptance-wrapper-failure-" + runId + ".json");
            string message = Escape(ex.ToString());
            File.WriteAllText(markerPath,
                "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Main\",\"acceptance_invoked\":false,\"wrapper_error\":\"" +
                message + "\"}", new System.Text.UTF8Encoding(false));
            string failure = "{\"test\":\"F11 NX acceptance wrapper\",\"passed\":false,\"error\":\"" + message +
                "\",\"reproduction_steps\":[\"Compile the production M2 assemblies and F11Acceptance.dll offline.\",\"Run RunF11Acceptance.py in the root-designated idle NX2506 session with the unique F11 fixture path.\"],\"retest_criterion\":\"Preserve this failure report; correct the assembly loading or invocation fault; rerun F11 in a fresh idle NX2506 session using new unique model and report paths.\"}";
            File.WriteAllText(failurePath, failure, new System.Text.UTF8Encoding(false));
            throw;
        }
    }

    public static int Run() { Main(new string[0]); return 0; }
    public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Explicitly; }

    private static string Escape(string value)
    {
        if (value == null) return String.Empty;
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string simpleName = new AssemblyName(args.Name).Name + ".dll";
        string productionBuild = Path.Combine(ProjectRoot, "workspace", "build", "current", "M2");
        string nxManaged = @"C:\0sf\ug\NXBIN\managed";
        foreach (string root in new string[] { productionBuild, nxManaged })
        {
            string candidate = Path.Combine(root, simpleName);
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }
}
