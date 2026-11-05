// NX journal loader for F03Acceptance.dll. RunF03Acceptance.py invokes Run()
// through NXOpen.Session.Execute so NX loads this exact wrapper assembly.
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
        string markerPath = Path.Combine(evidenceDirectory,
            "F03-journal-start-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(markerPath,
            "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Main\",\"started_utc\":\"" +
            DateTime.UtcNow.ToString("o") + "\",\"acceptance_invoked\":false}",
            new System.Text.UTF8Encoding(false));
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            string assemblyPath = Path.Combine(evidenceDirectory, "F03Acceptance.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Compile tests/nx/v2/build-f03-acceptance.ps1 before running this journal.", assemblyPath);
            Assembly acceptance = Assembly.LoadFrom(assemblyPath);
            Type entry = acceptance.GetType("F03Acceptance", true, false);
            MethodInfo main = entry.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
            if (main == null) throw new MissingMethodException("F03Acceptance.Main(string[]) was not found.");
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
            Directory.CreateDirectory(evidenceDirectory);
            string failurePath = Path.Combine(evidenceDirectory,
                "F03-acceptance-wrapper-failure-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(markerPath,
                "{\"entry_class\":\"NXJournal\",\"entry_method\":\"Main\",\"started_utc\":\"" +
                DateTime.UtcNow.ToString("o") + "\",\"acceptance_invoked\":false,\"wrapper_error\":\"" +
                Escape(ex.ToString()) + "\"}", new System.Text.UTF8Encoding(false));
            string report = "{\"test\":\"F03 NX acceptance wrapper\",\"passed\":false,\"error\":\"" +
                Escape(ex.ToString()) + "\",\"reproduction_steps\":[\"Build production M2 assemblies and F03Acceptance.dll.\",\"Load RunF03Acceptance.cs with the configured run_journal.exe.\"],\"retest_criterion\":\"Correct the assembly loading or invocation fault, then rerun the complete acceptance on a new unique model path.\"}";
            File.WriteAllText(failurePath, report, new System.Text.UTF8Encoding(false));
            throw;
        }
    }

    public static int Run()
    {
        Main(new string[0]);
        return 0;
    }

    private static string Escape(string value)
    {
        if (value == null) return "";
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

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Explicitly;
    }
}
