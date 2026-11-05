using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using NXOpen;

/// <summary>Native result of a two-curve sketch fillet.</summary>
public sealed class V2SketchFilletResult
{
    public Arc Geometry;
    public SketchConstraint[] Constraints;
    public int NativeConstraintsReturnedCount;
}

/// <summary>
/// Native result of a two-line sketch chamfer. CommittedObjects are the NX builder's
/// committed-object results and are not guaranteed to be the newly created sketch curves.
/// </summary>
public sealed class V2SketchChamferResult
{
    public NXObject[] CommittedObjects;
    public SketchConstraint[] Constraints;
    public string ChamferOptionBeforeCommit;
    public string[] CurvesToChamferTagStringsBeforeCommit;
    public string Distance1RightHandSideBeforeCommit;
    public string Distance2RightHandSideBeforeCommit;
    public string AngleRightHandSideBeforeCommit;
    public string ChamferOptionAfterCommit;
    public string[] CurvesToChamferTagStringsAfterCommit;
    public string Distance1RightHandSideAfterCommit;
    public string Distance2RightHandSideAfterCommit;
    public string AngleRightHandSideAfterCommit;
    public string PostCommitReadbackError;
}

/// <summary>Available NX native sketch chamfer definitions.</summary>
public enum V2SketchChamferMode
{
    Symmetric,
    Asymmetric,
    OffsetAndAngle
}

/// <summary>Creates native sketch fillets and chamfers for the V2 operation adapter.</summary>
public static class V2SketchCornerFactory
{
    /// <summary>
    /// Creates a native fillet from two explicit sketch curves. Each help point must lie on
    /// its corresponding curve and selects the end/side to modify.
    /// </summary>
    public static V2SketchFilletResult CreateFillet(Sketch sketch, Curve curve1, Curve curve2,
        Point3d helpPoint1, Point3d helpPoint2, double radius, bool alternateSolution)
    {
        if (sketch == null) throw new ArgumentNullException("sketch");
        RequireFilletCurve(sketch, curve1, "curve1");
        RequireFilletCurve(sketch, curve2, "curve2");
        if (curve1.Tag.Equals(curve2.Tag))
            throw new ArgumentException("A sketch fillet requires two distinct native curves.");
        RequireFinite(helpPoint1.X, "helpPoint1.X");
        RequireFinite(helpPoint1.Y, "helpPoint1.Y");
        RequireFinite(helpPoint1.Z, "helpPoint1.Z");
        RequireFinite(helpPoint2.X, "helpPoint2.X");
        RequireFinite(helpPoint2.Y, "helpPoint2.Y");
        RequireFinite(helpPoint2.Z, "helpPoint2.Z");
        RequirePositiveFinite(radius, "radius");

        SketchConstraint[] constraints;
        Arc[] arcs = sketch.Fillet(curve1, curve2, helpPoint1, helpPoint2, radius,
            Sketch.TrimInputOption.True, Sketch.CreateDimensionOption.False,
            alternateSolution ? Sketch.AlternateSolutionOption.True : Sketch.AlternateSolutionOption.False,
            out constraints);
        if (arcs == null || arcs.Length != 1 || arcs[0] == null)
            throw new InvalidOperationException("NX sketch fillet did not return exactly one Arc.");

        return new V2SketchFilletResult
        {
            Geometry = arcs[0],
            Constraints = constraints ?? new SketchConstraint[0],
            NativeConstraintsReturnedCount = constraints == null ? 0 : constraints.Length
        };
    }

    /// <summary>
    /// Creates a native chamfer between two explicit sketch Lines. The help point is supplied
    /// in work-part coordinates. Angle values are passed to NX unchanged in degrees.
    /// </summary>
    public static V2SketchChamferResult CreateChamfer(Part part, Sketch sketch,
        Line line1, Line line2, V2SketchChamferMode mode, double distance1,
        double distance2, double angleDegrees, Point3d helpPoint)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (sketch == null) throw new ArgumentNullException("sketch");
        RequireSketchLine(sketch, line1, "line1");
        RequireSketchLine(sketch, line2, "line2");
        if (line1.Tag.Equals(line2.Tag))
            throw new ArgumentException("A sketch chamfer requires two distinct native Lines.");
        RequirePositiveFinite(distance1, "distance1");
        if (mode == V2SketchChamferMode.Asymmetric)
            RequirePositiveFinite(distance2, "distance2");
        else if (mode != V2SketchChamferMode.Symmetric && mode != V2SketchChamferMode.OffsetAndAngle)
            throw new ArgumentOutOfRangeException("mode");
        if (mode == V2SketchChamferMode.OffsetAndAngle)
            RequireFinite(angleDegrees, "angleDegrees");
        RequireFinite(helpPoint.X, "helpPoint.X");
        RequireFinite(helpPoint.Y, "helpPoint.Y");
        RequireFinite(helpPoint.Z, "helpPoint.Z");

        SketchChamferBuilder builder = null;
        Point nativeHelpPoint = null;
        NXObject primaryObject = null;
        NXObject[] committedObjects = null;
        Exception operationException = null;
        ChamferBuilderSnapshot beforeCommit = null;
        ChamferBuilderSnapshot afterCommit = null;
        string postCommitReadbackError = null;
        try
        {
            builder = part.Sketches.CreateSketchChamferBuilder();
            nativeHelpPoint = part.Points.CreatePoint(helpPoint);
            builder.CurvesToChamfer.Add(line1);
            builder.CurvesToChamfer.Add(line2);
            builder.HelpPoint = nativeHelpPoint;
            builder.TrimInputCurves = true;
            builder.CreateDistance1Dimension = false;
            builder.CreateDistance2Dimension = false;
            builder.CreateAngleDimension = false;

            if (mode == V2SketchChamferMode.Symmetric)
                builder.ChamferOption = SketchChamferBuilder.ChamferOptions.Symmetric;
            else if (mode == V2SketchChamferMode.Asymmetric)
                builder.ChamferOption = SketchChamferBuilder.ChamferOptions.Asymmetric;
            else
                builder.ChamferOption = SketchChamferBuilder.ChamferOptions.OffsetandAngle;

            builder.Distance1.RightHandSide = NumberText(distance1);
            if (mode == V2SketchChamferMode.Asymmetric)
                builder.Distance2.RightHandSide = NumberText(distance2);
            if (mode == V2SketchChamferMode.OffsetAndAngle)
            {
                // The adapter/native probe owns the interpretation of this NX angle value.
                // Preserve the requested degree value without converting or re-deriving it.
                builder.Angle.RightHandSide = NumberText(angleDegrees);
            }

            beforeCommit = CaptureChamferBuilder(builder);
            primaryObject = builder.Commit();
            committedObjects = builder.GetCommittedObjects();
            try
            {
                afterCommit = CaptureChamferBuilder(builder);
            }
            catch (Exception ex)
            {
                postCommitReadbackError = ex.GetType().Name + ": " + ex.Message;
            }
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

        if (operationException != null && destroyException != null)
            throw new AggregateException("NX sketch chamfer operation and builder cleanup both failed.",
                operationException, destroyException);
        if (operationException != null)
            ExceptionDispatchInfo.Capture(operationException).Throw();
        if (destroyException != null)
            throw new InvalidOperationException(
                "NX sketch chamfer completed but its builder could not be destroyed.", destroyException);

        // The helper is only an input used to choose the native chamfer solution. Remove it
        // after a successful commit; failures are left intact for the adapter's undo mark.
        if (nativeHelpPoint != null) part.Points.DeletePoint(nativeHelpPoint);

        NXObject[] allCommitted = MergeCommittedObjects(primaryObject, committedObjects);
        if (allCommitted.Length == 0)
            throw new InvalidOperationException("NX sketch chamfer did not return a committed object.");

        return new V2SketchChamferResult
        {
            CommittedObjects = allCommitted,
            Constraints = FindConstraints(allCommitted),
            ChamferOptionBeforeCommit = beforeCommit.ChamferOption,
            CurvesToChamferTagStringsBeforeCommit = beforeCommit.CurveTagStrings,
            Distance1RightHandSideBeforeCommit = beforeCommit.Distance1RightHandSide,
            Distance2RightHandSideBeforeCommit = beforeCommit.Distance2RightHandSide,
            AngleRightHandSideBeforeCommit = beforeCommit.AngleRightHandSide,
            ChamferOptionAfterCommit = afterCommit == null ? null : afterCommit.ChamferOption,
            CurvesToChamferTagStringsAfterCommit = afterCommit == null ? null : afterCommit.CurveTagStrings,
            Distance1RightHandSideAfterCommit = afterCommit == null ? null : afterCommit.Distance1RightHandSide,
            Distance2RightHandSideAfterCommit = afterCommit == null ? null : afterCommit.Distance2RightHandSide,
            AngleRightHandSideAfterCommit = afterCommit == null ? null : afterCommit.AngleRightHandSide,
            PostCommitReadbackError = postCommitReadbackError
        };
    }

    private sealed class ChamferBuilderSnapshot
    {
        internal string ChamferOption;
        internal string[] CurveTagStrings;
        internal string Distance1RightHandSide;
        internal string Distance2RightHandSide;
        internal string AngleRightHandSide;
    }

    private static ChamferBuilderSnapshot CaptureChamferBuilder(SketchChamferBuilder builder)
    {
        DisplayableObject[] curves = builder.CurvesToChamfer.GetArray();
        string[] curveTags = new string[curves == null ? 0 : curves.Length];
        for (int i = 0; i < curveTags.Length; i++)
            curveTags[i] = curves[i] == null ? null : curves[i].Tag.ToString();

        return new ChamferBuilderSnapshot
        {
            ChamferOption = builder.ChamferOption.ToString(),
            CurveTagStrings = curveTags,
            Distance1RightHandSide = builder.Distance1.RightHandSide,
            Distance2RightHandSide = builder.Distance2.RightHandSide,
            AngleRightHandSide = builder.Angle.RightHandSide
        };
    }

    private static void RequireFilletCurve(Sketch sketch, Curve curve, string name)
    {
        if (curve == null) throw new ArgumentNullException(name);
        if (!(curve is Line) && !(curve is Arc))
            throw new ArgumentException(name + " must be a native Line or Arc.", name);
        if (!sketch.IsSketchGeometry(curve))
            throw new ArgumentException(name + " must belong to the supplied sketch.", name);
    }

    private static void RequireSketchLine(Sketch sketch, Line line, string name)
    {
        if (line == null) throw new ArgumentNullException(name);
        if (!sketch.IsSketchGeometry(line))
            throw new ArgumentException(name + " must belong to the supplied sketch.", name);
    }

    private static NXObject[] MergeCommittedObjects(NXObject primary, NXObject[] committed)
    {
        List<NXObject> result = new List<NXObject>();
        if (committed != null)
            for (int i = 0; i < committed.Length; i++) AddUnique(result, committed[i]);
        AddUnique(result, primary);
        return result.ToArray();
    }

    private static void AddUnique(List<NXObject> objects, NXObject candidate)
    {
        if (candidate == null) return;
        for (int i = 0; i < objects.Count; i++)
            if (objects[i].Tag.Equals(candidate.Tag)) return;
        objects.Add(candidate);
    }

    private static SketchConstraint[] FindConstraints(NXObject[] objects)
    {
        List<SketchConstraint> result = new List<SketchConstraint>();
        for (int i = 0; i < objects.Length; i++)
        {
            SketchConstraint constraint = objects[i] as SketchConstraint;
            if (constraint != null) result.Add(constraint);
        }
        return result.ToArray();
    }

    private static string NumberText(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void RequirePositiveFinite(double value, string name)
    {
        RequireFinite(value, name);
        if (value <= 0.0) throw new ArgumentOutOfRangeException(name, "Value must be greater than zero.");
    }

    private static void RequireFinite(double value, string name)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, "Value must be finite.");
    }
}
