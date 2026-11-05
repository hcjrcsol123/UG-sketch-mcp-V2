using System;
using System.Collections.Generic;
using System.Reflection;
using NXOpen;
using NXOpen.UF;

internal static class F12ProfileContractHarness
{
    private static int _checks;
    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length != 1) return 64;
        string nxManaged = System.IO.Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
        {
            string file = new AssemblyName(eventArgs.Name).Name + ".dll";
            string candidate = System.IO.Path.Combine(nxManaged, file);
            return System.IO.File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
        try
        {
        Type analyzer = typeof(V2SketchProfileAnalyzer);
        MethodInfo fingerprint = analyzer.GetMethod("F12DomainFingerprint", BindingFlags.Public | BindingFlags.Static);
        MethodInfo equivalent = analyzer.GetMethod("F12GeometryRecordsEquivalent", BindingFlags.Public | BindingFlags.Static);
        Check("public fingerprint seam", fingerprint != null);
        Check("public complete-geometry comparer seam", equivalent != null);
        if (fingerprint == null || equivalent == null) return 1;

        Dictionary<string, object> line = Line(new double[] { 1, 2, 3 }, new double[] { 8, 2, 3 });
        Dictionary<string, object> reversedLine = Line(new double[] { 8, 2, 3 }, new double[] { 1, 2, 3 });
        Equivalent(equivalent, "reversed line endpoints", line, reversedLine, 1.0e-6, true, "UNORDERED_ENDPOINTS");
        EqualFingerprint(fingerprint, "line endpoint order fingerprint", new[] { line }, new[] { reversedLine });

        Dictionary<string, object> fullArc = Arc(new double[] { 0, 0, 0 }, 5, new double[] { 0, 0, 1 }, true, null);
        Dictionary<string, object> reversedFullArc = Arc(new double[] { 0, 0, 0 }, 5, new double[] { 0, 0, -1 }, true, null);
        Equivalent(equivalent, "full circle normal reversal", fullArc, reversedFullArc, 1.0e-6, true,
            "CIRCLE_SUPPORT_AND_ORIENTATION_INSENSITIVE_DOMAIN");
        EqualFingerprint(fingerprint, "full circle orientation fingerprint", new[] { fullArc }, new[] { reversedFullArc });

        double[][] arcSamples = new double[][] {
            new double[] { 5, 0, 0 }, new double[] { 3.5355339059, 3.5355339059, 0 },
            new double[] { 0, 5, 0 }, new double[] { -3.5355339059, 3.5355339059, 0 }, new double[] { -5, 0, 0 }
        };
        Dictionary<string, object> partialArc = Arc(new double[] { 0, 0, 0 }, 5, new double[] { 0, 0, 1 }, false, arcSamples);
        Dictionary<string, object> reversePartialArc = Arc(new double[] { 0, 0, 0 }, 5, new double[] { 0, 0, -1 }, false, Reverse(arcSamples));
        Equivalent(equivalent, "partial arc reversed parameter/domain", partialArc, reversePartialArc, 1.0e-6, true,
            "CIRCLE_SUPPORT_AND_ORIENTATION_INSENSITIVE_DOMAIN");
        double[][] wrongArcSamples = Clone(arcSamples); wrongArcSamples[2] = new double[] { 0, -5, 0 };
        Equivalent(equivalent, "partial arc wrong sector", partialArc,
            Arc(new double[] { 0, 0, 0 }, 5, new double[] { 0, 0, 1 }, false, wrongArcSamples), 1.0e-6, false, null);

        Dictionary<string, object> fullEllipse = Ellipse(new double[] { 2, -3, 4 }, 9, 3, new double[] { 0, 0, 1 },
            new double[] { 0.8, 0.6, 0 }, true, null);
        Dictionary<string, object> reversedEllipse = Ellipse(new double[] { 2, -3, 4 }, 9, 3, new double[] { 0, 0, -1 },
            new double[] { -0.8, -0.6, 0 }, true, null);
        Equivalent(equivalent, "full ellipse axis/normal reversal", fullEllipse, reversedEllipse, 1.0e-6, true,
            "ELLIPSE_SUPPORT_AND_ORIENTATION_INSENSITIVE_DOMAIN");
        EqualFingerprint(fingerprint, "full ellipse orientation fingerprint", new[] { fullEllipse }, new[] { reversedEllipse });

        double[][] ellipseSamples = new double[][] {
            new double[] { 9, 0, 0 }, new double[] { 6.3639610307, 2.1213203436, 0 },
            new double[] { 0, 3, 0 }, new double[] { -6.3639610307, 2.1213203436, 0 }, new double[] { -9, 0, 0 }
        };
        Dictionary<string, object> partialEllipse = Ellipse(new double[] { 0, 0, 0 }, 9, 3, new double[] { 0, 0, 1 },
            new double[] { 1, 0, 0 }, false, ellipseSamples);
        Dictionary<string, object> reversePartialEllipse = Ellipse(new double[] { 0, 0, 0 }, 9, 3, new double[] { 0, 0, -1 },
            new double[] { -1, 0, 0 }, false, Reverse(ellipseSamples));
        Equivalent(equivalent, "partial ellipse reversed domain", partialEllipse, reversePartialEllipse, 1.0e-6, true,
            "ELLIPSE_SUPPORT_AND_ORIENTATION_INSENSITIVE_DOMAIN");

        Dictionary<string, object> spline = Spline(false, 0.3);
        Dictionary<string, object> reversedSpline = Spline(true, 0.3);
        MethodInfo nurbsMatch = analyzer.GetMethod("F12NurbsDefinitionsMatch", BindingFlags.NonPublic | BindingFlags.Static);
        Check("complete NURBS comparer accepts reverse parameterization",
            nurbsMatch != null && (bool)nurbsMatch.Invoke(null, new object[] { spline, reversedSpline, 1.0e-6 }));
        Equivalent(equivalent, "spline identical complete NURBS definition", spline, Spline(false, 0.3), 1.0e-6, true,
            "CARTESIAN_POLES_POSITIVE_RELATIVE_WEIGHTS_KNOTS_AND_DOMAIN_SAMPLES");
        Dictionary<string, object> symmetricSpline = Spline(false, 0.5);
        Dictionary<string, object> reversedSymmetricSpline = Spline(true, 0.5);
        Equivalent(equivalent, "spline reversed uniform interior knot", symmetricSpline, reversedSymmetricSpline, 1.0e-6, true,
            "CARTESIAN_POLES_POSITIVE_RELATIVE_WEIGHTS_KNOTS_AND_DOMAIN_SAMPLES");
        EqualFingerprint(fingerprint, "symmetric spline direction fingerprint",
            new[] { symmetricSpline }, new[] { reversedSymmetricSpline });
        Equivalent(equivalent, "complete spline NURBS reverse orientation", spline, reversedSpline, 1.0e-6, true,
            "CARTESIAN_POLES_POSITIVE_RELATIVE_WEIGHTS_KNOTS_AND_DOMAIN_SAMPLES");
        EqualFingerprint(fingerprint, "spline direction fingerprint", new[] { spline }, new[] { reversedSpline });
        Dictionary<string, object> alteredSpline = Spline(false, 0.3);
        Dictionary<string, object> alteredNurbs = (Dictionary<string, object>)alteredSpline["nurbs_definition"];
        List<object> alteredPoles = (List<object>)alteredNurbs["poles"];
        List<object> alteredPole = (List<object>)alteredPoles[2];
        alteredPole[1] = 99.0;
        Equivalent(equivalent, "spline changed control point rejected", spline, alteredSpline, 1.0e-6, false, null);

        IReadOnlyDomainChecks(fingerprint, line, reversedLine);
        Console.WriteLine("f12_profile_contract_checks=" + _checks + " failures=" + _failures);
        return _failures == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FATAL " + exception.GetType().FullName + ": " + exception.Message);
            Console.Error.WriteLine(exception.StackTrace);
            return 1;
        }
    }

    private static void IReadOnlyDomainChecks(MethodInfo method, Dictionary<string, object> line,
        Dictionary<string, object> reversedLine)
    {
        string lineFingerprint = Convert.ToString(method.Invoke(null, new object[] {
            new List<Dictionary<string, object>> { line }
        }), System.Globalization.CultureInfo.InvariantCulture);
        string duplicateFingerprint = Convert.ToString(method.Invoke(null, new object[] {
            new List<Dictionary<string, object>> { line, reversedLine }
        }), System.Globalization.CultureInfo.InvariantCulture);
        Check("duplicate curve remains represented in domain fingerprint", !String.Equals(lineFingerprint, duplicateFingerprint, StringComparison.Ordinal));
    }

    private static void Equivalent(MethodInfo method, string label, Dictionary<string, object> first,
        Dictionary<string, object> second, double tolerance, bool expected, string matchMode)
    {
        object[] arguments = new object[] { first, second, tolerance, null };
        bool actual = (bool)method.Invoke(null, arguments);
        Dictionary<string, object> evidence = (Dictionary<string, object>)arguments[3];
        Check(label, actual == expected, evidence);
        if (expected && matchMode != null)
        {
            object observed;
            bool present = evidence.TryGetValue("match_mode", out observed);
            Check(label + " match mode", present && Object.Equals(observed, matchMode), evidence);
        }
    }

    private static void EqualFingerprint(MethodInfo method, string label,
        Dictionary<string, object>[] first, Dictionary<string, object>[] second)
    {
        string a = Convert.ToString(method.Invoke(null, new object[] { new List<Dictionary<string, object>>(first) }),
            System.Globalization.CultureInfo.InvariantCulture);
        string b = Convert.ToString(method.Invoke(null, new object[] { new List<Dictionary<string, object>>(second) }),
            System.Globalization.CultureInfo.InvariantCulture);
        Check(label, String.Equals(a, b, StringComparison.Ordinal), new Dictionary<string, object> { { "first", a }, { "second", b } });
    }

    private static Dictionary<string, object> Line(double[] start, double[] end)
    {
        return new Dictionary<string, object> { { "kind", "LINE" }, { "start", start }, { "end", end } };
    }

    private static Dictionary<string, object> Arc(double[] center, double radius, double[] normal,
        bool closed, double[][] samples)
    {
        Dictionary<string, object> result = new Dictionary<string, object> {
            { "kind", "ARC" }, { "center", center }, { "radius_mm", radius }, { "normal", normal },
            { "is_closed_domain", closed }
        };
        if (samples != null) result["domain_samples"] = Samples(samples);
        return result;
    }

    private static Dictionary<string, object> Ellipse(double[] center, double major, double minor,
        double[] normal, double[] majorAxis, bool closed, double[][] samples)
    {
        Dictionary<string, object> result = new Dictionary<string, object> {
            { "kind", "ELLIPSE" }, { "center", center }, { "major_radius_mm", major },
            { "minor_radius_mm", minor }, { "normal", normal }, { "major_axis", majorAxis },
            { "is_closed_domain", closed }
        };
        if (samples != null) result["domain_samples"] = Samples(samples);
        return result;
    }

    private static Dictionary<string, object> Spline(bool reversed, double middleKnot)
    {
        double[][] poles = new double[][] {
            new double[] { 0, 0, 0 }, new double[] { 2, 1, 0 }, new double[] { 4, 3, 0 },
            new double[] { 6, 2, 0 }, new double[] { 8, 0, 0 }
        };
        if (reversed) Array.Reverse(poles);
        double[] knots = reversed
            ? new double[] { 0, 0, 0, 0, 1.0 - middleKnot, 1, 1, 1, 1 }
            : new double[] { 0, 0, 0, 0, middleKnot, 1, 1, 1, 1 };
        List<object> poleRecords = new List<object>();
        foreach (double[] pole in poles) poleRecords.Add(new List<object> { pole[0], pole[1], pole[2] });
        List<object> knotRecords = new List<object>(); foreach (double knot in knots) knotRecords.Add(knot);
        return new Dictionary<string, object> {
            { "kind", "SPLINE" },
            { "nurbs_definition", new Dictionary<string, object> {
                { "degree", 3 }, { "order", 4 }, { "pole_count", 5 }, { "rational", false },
                { "knots", knotRecords }, { "poles", poleRecords },
                { "start_parameter", 0.0 }, { "end_parameter", 1.0 }
            }}
        };
    }

    private static List<object> Samples(double[][] samples)
    {
        List<object> result = new List<object>(); foreach (double[] sample in samples) result.Add(sample);
        return result;
    }

    private static double[][] Reverse(double[][] source)
    {
        double[][] result = Clone(source); Array.Reverse(result); return result;
    }

    private static double[][] Clone(double[][] source)
    {
        double[][] result = new double[source.Length][];
        for (int i = 0; i < source.Length; i++) result[i] = (double[])source[i].Clone();
        return result;
    }

    private static void Check(string label, bool passed, object evidence = null)
    {
        _checks++;
        if (passed) return;
        _failures++;
        string detail = "null";
        Dictionary<string, object> dictionary = evidence as Dictionary<string, object>;
        if (dictionary != null)
        {
            List<string> fields = new List<string>();
            foreach (KeyValuePair<string, object> item in dictionary) fields.Add(item.Key + "=" + Convert.ToString(item.Value, System.Globalization.CultureInfo.InvariantCulture));
            detail = String.Join(",", fields.ToArray());
        }
        Console.Error.WriteLine("FAIL " + label + " evidence=" + detail);
    }
}
