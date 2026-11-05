using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NxSketchMcp.M2;

/// <summary>
/// NX-free reflection harness for the production F09 helper-inventory clone and
/// M2 JSON writer. It invokes PatternHelperCleanupMatches and PatternCloneValue
/// from the built M2Entry assembly; it does not instantiate an NX session.
/// </summary>
public static class F09HelperSerializationHarness
{
    public static int Main(string[] args)
    {
        if (args == null || args.Length != 3)
        {
            Console.Error.WriteLine("usage: F09HelperSerializationHarness <M2Entry.dll> <NX managed dir> <case>");
            return 2;
        }

        string nxManaged = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            string name;
            try { name = new AssemblyName(eventArgs.Name).Name + ".dll"; }
            catch { return null; }
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
            if (File.Exists(local)) return Assembly.LoadFrom(local);
            string nxPath = Path.Combine(nxManaged, name);
            return File.Exists(nxPath) ? Assembly.LoadFrom(nxPath) : null;
        };

        try
        {
            Assembly entryAssembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type adapterType = entryAssembly.GetType("M2OpenModelAdapter", true, false);
            Type planType = adapterType.GetNestedType("M3SketchPatternPlanState", BindingFlags.NonPublic);
            if (planType == null) throw new InvalidOperationException("F09 plan state type was not found in M2Entry.dll");

            Dictionary<string, List<string>> before;
            Dictionary<string, List<string>> created;
            Dictionary<string, List<string>> scheduled;
            Dictionary<string, List<string>> after;
            MakeCase(args[2], out before, out created, out scheduled, out after);

            object plan = Activator.CreateInstance(planType, true);
            SetField(planType, plan, "PartHelperTagsBefore", before);
            SetField(planType, plan, "PartHelperTagsAfter", after);
            Dictionary<string, object> factory = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "helper_tags_before", PublicInventory(before) },
                { "helper_tags_created", PublicInventory(created) },
                { "helper_tags_delete_scheduled", PublicInventory(scheduled) },
            };
            Dictionary<string, object> nativeOperation = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "factory", factory },
                { "commit_returned_null", true },
                { "output_curve_count", 1 },
            };
            SetField(planType, plan, "NativeOperationEvidence", nativeOperation);

            MethodInfo cleanupMethod = adapterType.GetMethod("PatternHelperCleanupMatches",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (cleanupMethod == null) throw new InvalidOperationException("production PatternHelperCleanupMatches was not found");
            object[] cleanupArgs = new object[] { plan, after, null };
            bool cleanupPassed = (bool)cleanupMethod.Invoke(null, cleanupArgs);
            Dictionary<string, object> cleanupEvidence = cleanupArgs[2] as Dictionary<string, object>;
            if (!cleanupPassed || cleanupEvidence == null)
                throw new InvalidOperationException("production PatternHelperCleanupMatches rejected the valid " + args[2] + " case");

            MethodInfo cloneMethod = adapterType.GetMethod("PatternCloneValue", BindingFlags.Static | BindingFlags.NonPublic);
            if (cloneMethod == null) throw new InvalidOperationException("production PatternCloneValue was not found");
            Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "pattern_instances", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "status", "VERIFIED" },
                        { "evidence", cleanupEvidence },
                    }
                }
            };
            Dictionary<string, object> resultShape = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "native_operation", nativeOperation },
                { "checks", checks },
            };
            object cloned = cloneMethod.Invoke(null, new object[] { resultShape });
            string canonical = M2Json.CanonicalText(cloned);

            Dictionary<string, object> rollbackShape = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "native_operation", nativeOperation },
                { "rollback", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "state", "ROLLED_BACK" },
                        { "undo_to_mark_succeeded", true },
                        { "helper_tags_restored", true },
                        { "helper_tags_before", before },
                        { "helper_tags_after", after },
                        { "helper_cleanup", cleanupEvidence },
                    }
                }
            };
            object rollbackClone = cloneMethod.Invoke(null, new object[] { rollbackShape });
            string rollbackCanonical = M2Json.CanonicalText(rollbackClone);
            Console.WriteLine("{\"success\":true,\"case\":" + M2Json.CanonicalText(args[2]) +
                ",\"cleanup\":" + canonical + ",\"rollback\":" + rollbackCanonical + "}");
            return 0;
        }
        catch (Exception ex)
        {
            Exception actual = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            Console.Error.WriteLine(actual.GetType().FullName + ": " + actual.Message);
            return 1;
        }
    }

    private static void MakeCase(string name,
        out Dictionary<string, List<string>> before,
        out Dictionary<string, List<string>> created,
        out Dictionary<string, List<string>> scheduled,
        out Dictionary<string, List<string>> after)
    {
        before = EmptyInventory();
        created = EmptyInventory();
        scheduled = EmptyInventory();
        after = EmptyInventory();
        if (name == "nonempty")
        {
            before["points"].Add("P0");
            before["axes"].Add("A0");
            before["directions"].Add("D0");
            created["points"].Add("P1");
            created["axes"].Add("A1");
            created["directions"].Add("D1");
            scheduled["points"].Add("P1");
            scheduled["axes"].Add("A1");
            scheduled["directions"].Add("D1");
            CopyInventory(before, after);
        }
        else if (name == "rollback")
        {
            before["points"].Add("P0");
            before["axes"].Add("A0");
            before["directions"].Add("D0");
            CopyInventory(before, after);
        }
        else if (name != "empty")
        {
            throw new ArgumentException("unknown F09 helper serialization case: " + name);
        }
    }

    private static Dictionary<string, List<string>> EmptyInventory()
    {
        return new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            { "points", new List<string>() },
            { "axes", new List<string>() },
            { "directions", new List<string>() },
        };
    }

    private static Dictionary<string, object> PublicInventory(Dictionary<string, List<string>> source)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "points", new List<object>(source["points"].ToArray()) },
            { "axes", new List<object>(source["axes"].ToArray()) },
            { "directions", new List<object>(source["directions"].ToArray()) },
        };
    }

    private static void CopyInventory(Dictionary<string, List<string>> source,
        Dictionary<string, List<string>> target)
    {
        foreach (string kind in source.Keys) target[kind] = new List<string>(source[kind]);
    }

    private static void SetField(Type type, object instance, string name, object value)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new InvalidOperationException("F09 plan field was not found: " + name);
        field.SetValue(instance, value);
    }
}
