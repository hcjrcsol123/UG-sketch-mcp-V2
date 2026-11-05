using System;
using System.Globalization;
using System.Runtime.ExceptionServices;
using NXOpen;
using NxSketchMcp.M2;

/// <summary>Native result from one NX SketchOffsetBuilder operation.</summary>
public sealed class V2SketchOffsetNativeResult
{
    public Curve[] OutputCurves;
    public bool ReverseDirectionUsed;
    public bool CommitReturnedNull;
    public string NativeCapType;
    public int SideEvaluations;
}

/// <summary>Creates native sketch offset curves using NX's SketchOffsetBuilder.</summary>
public static class V2SketchOffsetFactory
{
    /// <summary>
    /// Evaluates a positive offset, asks the caller whether its side matches the request,
    /// and reverses the native chain direction once when needed. The callback sees the
    /// evaluated, pre-commit curves. The returned curves are read back after commit.
    /// </summary>
    public static V2SketchOffsetNativeResult Execute(Part part, Curve[] orderedCurves,
        Point3d chainStart, double distanceMm, string joinMode,
        Func<Curve[], bool> matchesRequestedSide)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (orderedCurves == null) throw new ArgumentNullException("orderedCurves");
        if (orderedCurves.Length == 0)
            throw new ArgumentException("At least one ordered curve is required.", "orderedCurves");
        for (int i = 0; i < orderedCurves.Length; i++)
            if (orderedCurves[i] == null)
                throw new ArgumentException("orderedCurves cannot contain null entries.", "orderedCurves");
        Curve[] chainCurves = (Curve[])orderedCurves.Clone();
        RequireFinite(chainStart.X, "chainStart.X");
        RequireFinite(chainStart.Y, "chainStart.Y");
        RequireFinite(chainStart.Z, "chainStart.Z");
        RequirePositiveFinite(distanceMm, "distanceMm");
        if (joinMode != "extension" && joinMode != "arc")
            throw new ArgumentOutOfRangeException("joinMode", "joinMode must be extension or arc.");
        if (matchesRequestedSide == null) throw new ArgumentNullException("matchesRequestedSide");

        SketchOffsetBuilder builder = null;
        V2SketchOffsetNativeResult result = null;
        Exception operationException = null;
        try
        {
            builder = part.Sketches.CreateSketchOffsetBuilder(null);
            builder.CapType = joinMode == "arc"
                ? SketchOffset.CapType.Arc
                : SketchOffset.CapType.Extension;
            builder.ConvertToReference = false;
            builder.CreateConstraint = false;
            builder.CreateDimension = false;
            builder.IsSymmetric = false;
            builder.NumberOfCopies = 1;
            builder.Distance.RightHandSide = NumberText(distanceMm);

            Section section = builder.CreateSection();
            section.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
            section.AllowSelfIntersection(false);
            SelectionIntentRule rule = part.ScRuleFactory.CreateRuleCurveDumb(chainCurves);
            section.AddToSection(new SelectionIntentRule[] { rule }, chainCurves[0],
                null, null, chainStart, Section.Mode.Create, false);

            builder.UpdateLoopsAndCopies();
            builder.EvaluateOffset();
            Curve[] evaluatedCurves = ReadOutputCurves(builder, "before side evaluation");
            int sideEvaluations = 1;
            bool reverseDirectionUsed = false;
            bool matches = matchesRequestedSide((Curve[])evaluatedCurves.Clone());
            if (!matches)
            {
                builder.ReverseOffsetDirectionOfChain(chainCurves[0]);
                reverseDirectionUsed = true;
                builder.EvaluateOffset();
                evaluatedCurves = ReadOutputCurves(builder, "after reversing the chain direction");
                sideEvaluations++;
                matches = matchesRequestedSide((Curve[])evaluatedCurves.Clone());
                if (!matches)
                    throw new M2ModelException("OFFSET_SIDE_UNSUPPORTED",
                        "NX could not produce offset curves on the requested side after one chain-direction reversal");
            }

            string nativeCapType = builder.CapType.ToString();
            NXObject committed = builder.Commit();
            Curve[] outputCurves = ReadOutputCurves(builder, "after commit");
            result = new V2SketchOffsetNativeResult
            {
                OutputCurves = outputCurves,
                ReverseDirectionUsed = reverseDirectionUsed,
                CommitReturnedNull = committed == null,
                NativeCapType = nativeCapType,
                SideEvaluations = sideEvaluations
            };
        }
        catch (Exception ex)
        {
            operationException = ex;
        }

        Exception destroyException = null;
        if (builder != null)
        {
            try { builder.Destroy(); }
            catch (Exception ex) { destroyException = ex; }
        }

        // Preserve the operation exception, including a side callback exception, while
        // still attempting builder cleanup exactly once.
        if (operationException != null)
            ExceptionDispatchInfo.Capture(operationException).Throw();
        if (destroyException != null)
            throw new InvalidOperationException(
                "NX sketch offset completed but its builder could not be destroyed.", destroyException);
        return result;
    }

    private static Curve[] ReadOutputCurves(SketchOffsetBuilder builder, string stage)
    {
        NXObject[] nativeObjects = builder.GetOutputCurvesOfOffset();
        if (nativeObjects == null || nativeObjects.Length == 0)
            throw new M2ModelException("OFFSET_OUTPUT_INVALID",
                "NX returned no offset curves " + stage);
        Curve[] curves = new Curve[nativeObjects.Length];
        for (int i = 0; i < nativeObjects.Length; i++)
        {
            curves[i] = nativeObjects[i] as Curve;
            if (curves[i] == null)
                throw new M2ModelException("OFFSET_OUTPUT_INVALID",
                    "NX returned an invalid offset object " + stage + " (" +
                    (nativeObjects[i] == null ? "null" : nativeObjects[i].GetType().FullName) + ")");
        }
        return curves;
    }

    private static string NumberText(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void RequirePositiveFinite(double value, string name)
    {
        RequireFinite(value, name);
        if (value <= 0.0)
            throw new ArgumentOutOfRangeException(name, "Value must be greater than zero.");
    }

    private static void RequireFinite(double value, string name)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, "Value must be finite.");
    }
}
