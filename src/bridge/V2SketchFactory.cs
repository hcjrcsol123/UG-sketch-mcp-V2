using System;
using NXOpen;
using NXOpen.UF;
using NXOpen.Utilities;
using NxSketchMcp.M2;

/// <summary>Creates additional native sketch curves using the request's sketch-local frame.</summary>
public static class V2SketchFactory
{
    private const double DegreesToRadians = Math.PI / 180.0;

    public static Curve Create(Part part, Sketch sketch, M3SketchGeometryRequest geometry)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (sketch == null) throw new ArgumentNullException("sketch");
        if (geometry == null) throw new ArgumentNullException("geometry");
        if (geometry.Type == "arc") return CreateArc(part, sketch, geometry);
        if (geometry.Type == "ellipse") return CreateEllipse(part, sketch, geometry, false);
        if (geometry.Type == "elliptical_arc") return CreateEllipse(part, sketch, geometry, true);
        if (geometry.Type == "spline") return CreateSpline(part, sketch, geometry);
        throw new ArgumentException("Unsupported V2 sketch geometry type " + geometry.Type, "geometry");
    }

    private static Arc CreateArc(Part part, Sketch sketch, M3SketchGeometryRequest geometry)
    {
        SketchFrame frame = Frame(sketch, geometry.RotationDeg);
        Point3d center = ToWorld(frame, geometry.CenterMm[0], geometry.CenterMm[1]);
        double start = geometry.StartAngleDeg * DegreesToRadians;
        double end = (geometry.StartAngleDeg + geometry.SweepDeg) * DegreesToRadians;
        if (geometry.SweepDeg < 0.0)
        {
            frame.Y = Negate(frame.Y);
            double originalStart = start, originalEnd = end;
            start = -originalStart;
            end = -originalEnd;
        }
        Arc arc = part.Curves.CreateArc(center, frame.X, frame.Y, geometry.RadiusMm, start, end);
        sketch.AddGeometry(arc, Sketch.InferConstraintsOption.InferNoConstraints);
        return arc;
    }

    private static Ellipse CreateEllipse(Part part, Sketch sketch, M3SketchGeometryRequest geometry, bool partial)
    {
        SketchFrame baseFrame = Frame(sketch, 0.0);
        SketchFrame frame = Frame(sketch, geometry.RotationDeg);
        Point3d center = ToWorld(baseFrame, geometry.CenterMm[0], geometry.CenterMm[1]);
        double start = partial ? geometry.StartAngleDeg * DegreesToRadians : 0.0;
        double end = partial ? (geometry.StartAngleDeg + geometry.SweepDeg) * DegreesToRadians : 2.0 * Math.PI;
        if (partial && geometry.SweepDeg < 0.0)
        {
            frame.Y = Negate(frame.Y);
            double originalStart = start, originalEnd = end;
            start = -originalStart;
            end = -originalEnd;
        }
        if (partial)
        {
            // NX CreateEllipse takes polar angles. The F06 schema uses the ellipse
            // parameter t in x=a*cos(t), y=b*sin(t), so convert both endpoints and
            // unwrap the end in the positive native parameter direction.
            double polarStart = EllipsePolarAngle(start, geometry.MajorRadiusMm, geometry.MinorRadiusMm);
            double polarEnd = EllipsePolarAngle(end, geometry.MajorRadiusMm, geometry.MinorRadiusMm);
            while (polarEnd <= polarStart) polarEnd += 2.0 * Math.PI;
            start = polarStart;
            end = polarEnd;
        }
        Ellipse ellipse = part.Curves.CreateEllipse(center, frame.X, frame.Y,
            geometry.MajorRadiusMm, geometry.MinorRadiusMm, start, end);
        sketch.AddGeometry(ellipse, Sketch.InferConstraintsOption.InferNoConstraints);
        return ellipse;
    }

    private static double EllipsePolarAngle(double parameter, double majorRadius, double minorRadius)
    {
        return Math.Atan2(minorRadius * Math.Sin(parameter), majorRadius * Math.Cos(parameter));
    }

    private static Spline CreateSpline(Part part, Sketch sketch, M3SketchGeometryRequest geometry)
    {
        double[][] localPoints = geometry.InterpolationPointsMm;
        if (localPoints == null || localPoints.Length < 4)
            throw new ArgumentException("A cubic spline requires at least four interpolation points.", "geometry");
        SketchFrame frame = Frame(sketch, 0.0);
        UFCurve.PtSlopeCrvatr[] points = new UFCurve.PtSlopeCrvatr[localPoints.Length];
        double[] parameters = new double[localPoints.Length];
        double[] lengths = new double[localPoints.Length];
        for (int i = 0; i < localPoints.Length; i++)
        {
            if (localPoints[i] == null || localPoints[i].Length != 2)
                throw new ArgumentException("Spline interpolation points must contain two local coordinates.", "geometry");
            if (i > 0)
            {
                double dx = localPoints[i][0] - localPoints[i - 1][0];
                double dy = localPoints[i][1] - localPoints[i - 1][1];
                lengths[i] = lengths[i - 1] + Math.Sqrt(dx * dx + dy * dy);
            }
            Point3d world = ToWorld(frame, localPoints[i][0], localPoints[i][1]);
            points[i] = new UFCurve.PtSlopeCrvatr
            {
                point = new double[] { world.X, world.Y, world.Z },
                slope_type = -1,
                slope = new double[3],
                crvatr_type = -1,
                crvatr = new double[3]
            };
        }
        if (!(lengths[lengths.Length - 1] > 0.0) || Double.IsInfinity(lengths[lengths.Length - 1]))
            throw new ArgumentException("Spline interpolation points have no finite chord length.", "geometry");
        for (int i = 0; i < parameters.Length; i++) parameters[i] = lengths[i] / lengths[lengths.Length - 1];

        Tag tag;
        UFSession.GetUFSession().Curve.CreateSplineThruPts(3, 0, points.Length, points, parameters, 1, out tag);
        Spline spline = NXObjectManager.Get(tag) as Spline;
        if (spline == null) throw new InvalidOperationException("UF spline creation returned an object that is not an NX Spline.");
        sketch.AddGeometry(spline, Sketch.InferConstraintsOption.InferNoConstraints);
        return spline;
    }

    private sealed class SketchFrame
    {
        internal Point3d Origin;
        internal Vector3d X;
        internal Vector3d Y;
    }

    private static SketchFrame Frame(Sketch sketch, double rotationDeg)
    {
        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        double angle = rotationDeg * DegreesToRadians;
        double cosine = Math.Cos(angle);
        double sine = Math.Sin(angle);
        Vector3d x = new Vector3d(axes.Xx * cosine + axes.Yx * sine,
            axes.Xy * cosine + axes.Yy * sine, axes.Xz * cosine + axes.Yz * sine);
        Vector3d y = new Vector3d(axes.Yx * cosine - axes.Xx * sine,
            axes.Yy * cosine - axes.Xy * sine, axes.Yz * cosine - axes.Xz * sine);
        return new SketchFrame { Origin = origin, X = x, Y = y };
    }

    private static Point3d ToWorld(SketchFrame frame, double x, double y)
    {
        return new Point3d(frame.Origin.X + frame.X.X * x + frame.Y.X * y,
            frame.Origin.Y + frame.X.Y * x + frame.Y.Y * y,
            frame.Origin.Z + frame.X.Z * x + frame.Y.Z * y);
    }

    private static Vector3d Negate(Vector3d vector)
    {
        return new Vector3d(-vector.X, -vector.Y, -vector.Z);
    }

}
