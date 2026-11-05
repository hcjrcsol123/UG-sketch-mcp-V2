// Root-owned NX journal loader. This wrapper never starts or closes NX.
using System;
using System.IO;
using System.Reflection;
using NXOpen;

public class NXJournal
{
    public static int Run()
    {
        string harness = Environment.GetEnvironmentVariable("F19_ACCEPTANCE_DLL");
        if (String.IsNullOrEmpty(harness) || !File.Exists(harness))
            throw new FileNotFoundException("Set F19_ACCEPTANCE_DLL to the unique offline-compiled harness.", harness);
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        Assembly assembly = Assembly.LoadFrom(harness);
        MethodInfo main = assembly.GetType("F19Acceptance",true).GetMethod("Main",BindingFlags.Public|BindingFlags.Static);
        main.Invoke(null,new object[]{new string[0]});
        return 0; // Invocation status only; consult the harness report for test outcomes.
    }
    public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Explicitly; }
    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string build=Environment.GetEnvironmentVariable("F19_ADAPTER_BUILD_DIR");
        if (String.IsNullOrEmpty(build)) throw new InvalidOperationException("F19_ADAPTER_BUILD_DIR is required.");
        string name=new AssemblyName(args.Name).Name+".dll";
        foreach (string directory in new string[]{build,@"C:\0sf\ug\NXBIN\managed"})
        {
            string candidate=Path.Combine(directory,name);
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }
}
