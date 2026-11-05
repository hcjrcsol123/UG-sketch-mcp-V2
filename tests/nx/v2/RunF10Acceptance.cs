// NX journal loader for F10Acceptance.dll. RunF10Acceptance.py calls Session.Execute.
// This wrapper never starts, restarts, closes, or terminates NX.
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
        string markerPath = Path.Combine(evidenceDirectory, "F10-journal-start-" + runId + ".json");
        File.WriteAllText(markerPath,
            "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Main\",\"started_utc\":\"" +
            DateTime.UtcNow.ToString("o") + "\",\"acceptance_invoked\":false}",
            new System.Text.UTF8Encoding(false));
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            string assemblyPath = Path.Combine(evidenceDirectory, "F10Acceptance.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Compile tests/nx/v2/build-f10-acceptance.ps1 before running this journal.", assemblyPath);
            Assembly acceptance = Assembly.LoadFrom(assemblyPath);
            Type entry = acceptance.GetType("F10Acceptance", true, false);
            MethodInfo main = entry.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
            if (main == null) throw new MissingMethodException("F10Acceptance.Main(string[]) was not found.");
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
            string failurePath = Path.Combine(evidenceDirectory, "F10-acceptance-wrapper-failure-" + runId + ".json");
            string message = Escape(ex.ToString());
            File.WriteAllText(markerPath,
                "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Main\",\"acceptance_invoked\":false,\"wrapper_error\":\"" +
                message + "\"}", new System.Text.UTF8Encoding(false));
            string failure = "{\"test\":\"F10 NX acceptance wrapper\",\"passed\":false,\"error\":\"" + message +
                "\",\"reproduction_steps\":[\"Build the production M2 assemblies and F10Acceptance.dll offline.\",\"Run RunF10Acceptance.py in a designated idle NX2506 session with a saved, unmodified prior Work Part.\"],\"retest_criterion\":\"Preserve this failure report; correct the assembly loading or invocation fault; rerun F10 on a new unique model and report path.\"}";
            File.WriteAllText(failurePath, failure, new System.Text.UTF8Encoding(false));
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
        string[] roots = new string[] { productionBuild, nxManaged };
        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, simpleName);
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }
}
