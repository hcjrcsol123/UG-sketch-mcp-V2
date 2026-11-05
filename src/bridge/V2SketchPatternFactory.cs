using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using NXOpen;
using NXOpen.GeometricUtilities;
using NxSketchMcp.M2;

/// <summary>Creates geometry-only sketch arrays from native curves and mirrors with NX's mirror builder.</summary>
public static class V2SketchPatternFactory
{
    private const double TwoPi = 2.0 * Math.PI;

    private sealed class RigidTransform
    {
        internal Vector3d Translation;
        internal Point3d Pivot;
        internal Vector3d Axis;
        internal double AngleRad;
        internal bool Rotate;
    }

    private static List<RigidTransform> BuildTranslationTransforms(Sketch sketch,
        M3SketchPatternRequest request)
    {
        List<RigidTransform> transforms = new List<RigidTransform>();
        if (request.Mode == "linear")
        {
            Vector3d direction = WorldDirection(sketch, request.DirectionXY, "direction_xy");
            for (int index = 0; index < request.InstanceCount; index++)
            {
                transforms.Add(new RigidTransform
                {
                    Translation = Scale(direction, request.SpacingMm * index)
                });
            }
        }
        else if (request.Mode == "rectangular")
        {
            Vector3d xDirection = WorldDirection(sketch, request.DirectionXXY, "direction_x_xy");
            Vector3d yDirection = WorldDirection(sketch, request.DirectionYXY, "direction_y_xy");
            for (int yIndex = 0; yIndex < request.CountY; yIndex++)
                for (int xIndex = 0; xIndex < request.CountX; xIndex++)
                {
                    Vector3d xOffset = Scale(xDirection, request.SpacingXmm * xIndex);
                    Vector3d yOffset = Scale(yDirection, request.SpacingYmm * yIndex);
                    transforms.Add(new RigidTransform { Translation = Add(xOffset, yOffset) });
                }
        }
        else throw new M2ModelException("INVALID_REQUEST", "translation transforms require linear or rectangular mode");
        return transforms;
    }

    private static List<RigidTransform> BuildCircularTransforms(Point3d center, Vector3d normal,
        double angleStepRad, int instanceCount)
    {
        List<RigidTransform> transforms = new List<RigidTransform>();
        for (int index = 0; index < instanceCount; index++)
        {
            transforms.Add(new RigidTransform
            {
                Rotate = true,
                Pivot = center,
                Axis = normal,
                AngleRad = angleStepRad * index
            });
        }
        return transforms;
    }

    private static void CreateTransformedGeometry(Part part, Sketch sketch,
        Curve[] sourceCurves, List<RigidTransform> transforms)
    {
        if (transforms == null || transforms.Count < 2)
            throw new M2ModelException("INVALID_REQUEST", "pattern must contain a source position and at least one output position");
        for (int sourceIndex = 0; sourceIndex < sourceCurves.Length; sourceIndex++)
        {
            Curve source = sourceCurves[sourceIndex];
            if (source == null) throw new M2ModelException("PATTERN_INPUT_FAILED", "F09 source curve is null");
            if (!(source is Line) && !(source is Arc))
                throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F09 geometry-only arrays support native Line and circular Arc only");
            for (int positionIndex = 1; positionIndex < transforms.Count; positionIndex++)
            {
                Curve output = CreateTransformedCurve(part, source, transforms[positionIndex]);
                sketch.AddGeometry(output, Sketch.InferConstraintsOption.InferNoConstraints);
            }
        }
    }

    private static Curve CreateTransformedCurve(Part part, Curve source, RigidTransform transform)
    {
        Line line = source as Line;
        if (line != null)
        {
            Point3d start = TransformPoint(line.StartPoint, transform);
            Point3d end = TransformPoint(line.EndPoint, transform);
            if (!Finite(start) || !Finite(end))
                throw new M2ModelException("INVALID_REQUEST", "linear pattern produced a non-finite Line endpoint");
            Line result = part.Curves.CreateLine(start, end);
            if (result == null) throw new M2ModelException("PATTERN_OUTPUT_INVALID", "NX returned no Line for a requested geometry-only pattern output");
            return result;
        }

        Arc arc = source as Arc;
        if (arc != null)
        {
            Point3d center;
            Vector3d axisX, axisY;
            arc.GetOrientation(out center, out axisX, out axisY);
            Point3d transformedCenter = TransformPoint(center, transform);
            Vector3d transformedX = TransformVector(axisX, transform);
            Vector3d transformedY = TransformVector(axisY, transform);
            if (!Finite(transformedCenter) || !Finite(transformedX) || !Finite(transformedY))
                throw new M2ModelException("INVALID_REQUEST", "pattern produced non-finite Arc geometry");
            Arc result = part.Curves.CreateArc(transformedCenter, transformedX, transformedY,
                arc.Radius, arc.StartAngle, arc.EndAngle);
            if (result == null) throw new M2ModelException("PATTERN_OUTPUT_INVALID", "NX returned no Arc for a requested geometry-only pattern output");
            return result;
        }

        throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F09 geometry-only arrays support native Line and circular Arc only");
    }

    private static Point3d TransformPoint(Point3d point, RigidTransform transform)
    {
        if (!transform.Rotate)
            return new Point3d(point.X + transform.Translation.X,
                point.Y + transform.Translation.Y, point.Z + transform.Translation.Z);
        Vector3d relative = new Vector3d(point.X - transform.Pivot.X,
            point.Y - transform.Pivot.Y, point.Z - transform.Pivot.Z);
        Vector3d rotated = Rodrigues(relative, transform.Axis, transform.AngleRad);
        return new Point3d(transform.Pivot.X + rotated.X,
            transform.Pivot.Y + rotated.Y, transform.Pivot.Z + rotated.Z);
    }

    private static Vector3d TransformVector(Vector3d vector, RigidTransform transform)
    {
        return transform.Rotate ? Rodrigues(vector, transform.Axis, transform.AngleRad) : vector;
    }

    private static Vector3d Rodrigues(Vector3d value, Vector3d axis, double angleRad)
    {
        double cosine = Math.Cos(angleRad);
        double sine = Math.Sin(angleRad);
        Vector3d cross = Cross(axis, value);
        double dot = Dot(axis, value);
        return new Vector3d(value.X * cosine + cross.X * sine + axis.X * dot * (1.0 - cosine),
            value.Y * cosine + cross.Y * sine + axis.Y * dot * (1.0 - cosine),
            value.Z * cosine + cross.Z * sine + axis.Z * dot * (1.0 - cosine));
    }

    private static Vector3d Cross(Vector3d left, Vector3d right)
    {
        return new Vector3d(left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
    }

    private static double Dot(Vector3d left, Vector3d right)
    {
        return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    }

    private static Vector3d Scale(Vector3d value, double scale)
    {
        return new Vector3d(value.X * scale, value.Y * scale, value.Z * scale);
    }

    private static Vector3d Add(Vector3d left, Vector3d right)
    {
        return new Vector3d(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    }

    private static bool Finite(Point3d point)
    {
        return IsFinite(point.X) && IsFinite(point.Y) && IsFinite(point.Z);
    }

    private static bool Finite(Vector3d vector)
    {
        return IsFinite(vector.X) && IsFinite(vector.Y) && IsFinite(vector.Z);
    }

    public static V2SketchPatternNativeResult Execute(Part part, Sketch sketch,
        Curve[] sourceCurves, Curve mirrorAxis, M3SketchPatternRequest request)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (sketch == null) throw new ArgumentNullException("sketch");
        if (sourceCurves == null || sourceCurves.Length == 0)
            throw new ArgumentException("At least one source curve is required.", "sourceCurves");
        if (request == null) throw new ArgumentNullException("request");

        HashSet<string> beforeTags = SketchCurveTags(sketch);
        HashSet<string> globalCurveTagsBefore = PartCurveTags(part);
        Dictionary<string, List<string>> helperTagsBefore = PartHelperTagInventory(part);
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        NXObject committed = null;
        NXOpen.Builder builder = null;
        bool deleteHelpersScheduled = false;
        string builderType = null;
        string nativeEngine = null;
        bool createConstraint = false;
        bool createSpacingExpression = false;
        Exception operationException = null;
        try
        {
            if (request.Mode == "mirror")
            {
                if (mirrorAxis == null) throw new M2ModelException("CURVE_NOT_FOUND", "mirror mode requires a resolved axis Line");
                SketchMirrorPatternBuilder mirrorBuilder = part.Sketches.CreateSketchMirrorPatternBuilder(null);
                builder = mirrorBuilder;
                builderType = mirrorBuilder.GetType().Name;
                nativeEngine = "NXOpen.SketchMirrorPatternBuilder";
                AddInputSection(part, mirrorBuilder.Section, sourceCurves);
                mirrorBuilder.CreateConstraint = false;
                mirrorBuilder.ConvertToReference = false;
                mirrorBuilder.ConvertInputToReference = false;
                mirrorBuilder.DirectionObject.Value = mirrorAxis;
                mirrorBuilder.UpdateDirectionObject();
                mirrorBuilder.UpdateInputSection();
                createConstraint = mirrorBuilder.CreateConstraint;
                committed = mirrorBuilder.Commit();
                evidence["convert_input_to_reference"] = mirrorBuilder.ConvertInputToReference;
                evidence["convert_to_reference"] = mirrorBuilder.ConvertToReference;
                evidence["mirror_axis_tag"] = mirrorAxis.Tag.ToString();
                evidence["mirror_direction_object_tag"] = mirrorBuilder.DirectionObject.Value == null
                    ? null : mirrorBuilder.DirectionObject.Value.Tag.ToString();
            }
            else if (request.Mode == "linear" || request.Mode == "rectangular")
            {
                nativeEngine = "NXOpen.CurveCollection.CreateLine/CreateArc + Sketch.AddGeometry(InferNoConstraints)";
                Vector3d x = WorldDirection(sketch,
                    request.Mode == "linear" ? request.DirectionXY : request.DirectionXXY,
                    request.Mode == "linear" ? "direction_xy" : "direction_x_xy");
                evidence["direction_x_world"] = VectorRecord(x);
                evidence["count_x"] = request.Mode == "linear" ? request.InstanceCount : request.CountX;
                evidence["spacing_x_mm"] = request.Mode == "linear" ? request.SpacingMm : request.SpacingXmm;
                if (request.Mode == "rectangular")
                {
                    Vector3d y = WorldDirection(sketch, request.DirectionYXY, "direction_y_xy");
                    evidence["direction_y_world"] = VectorRecord(y);
                    evidence["count_y"] = request.CountY;
                    evidence["spacing_y_mm"] = request.SpacingYmm;
                }
                List<RigidTransform> transforms = BuildTranslationTransforms(sketch, request);
                CreateTransformedGeometry(part, sketch, sourceCurves, transforms);
            }
            else if (request.Mode == "circular")
            {
                nativeEngine = "NXOpen.CurveCollection.CreateLine/CreateArc + Sketch.AddGeometry(InferNoConstraints)";
                double[] centerXY = request.CenterXY;
                Point3d centerWorld = SketchPointToWorld(sketch, centerXY[0], centerXY[1]);
                Vector3d normal = SketchNormal(sketch);
                double angleStepRad = request.AngleStepDeg * Math.PI / 180.0;
                if (!IsFinite(request.AngleStepDeg) || request.AngleStepDeg == 0.0 || Math.Abs(request.AngleStepDeg) >= 360.0)
                    throw new M2ModelException("INVALID_REQUEST", "angle_step_deg must satisfy 0 < abs(angle_step_deg) < 360 degrees");
                evidence["pattern_type"] = "circular_geometry_only";
                evidence["rotation_center_mm"] = PointRecord(centerWorld);
                evidence["rotation_axis_direction_world"] = VectorRecord(normal);
                evidence["angle_step_deg_signed"] = request.AngleStepDeg;
                evidence["instance_count"] = request.InstanceCount;
                List<RigidTransform> transforms = BuildCircularTransforms(centerWorld, normal,
                    angleStepRad, request.InstanceCount);
                CreateTransformedGeometry(part, sketch, sourceCurves, transforms);
            }
            else
            {
                throw new M2ModelException("INVALID_REQUEST", "unsupported F09 pattern mode");
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

        if (operationException != null)
        {
            if (destroyException != null)
            {
                operationException.Data["nx_builder_destroy_exception_type"] = destroyException.GetType().FullName;
                operationException.Data["nx_builder_destroy_exception_message"] = destroyException.Message;
            }
            ExceptionDispatchInfo.Capture(operationException).Throw();
        }
        if (destroyException != null)
        {
            InvalidOperationException cleanupFailure = new InvalidOperationException(
                "NX sketch pattern completed, but its builder could not be destroyed; the adapter must roll back the operation.",
                destroyException);
            cleanupFailure.Data["nx_builder_destroy_exception_type"] = destroyException.GetType().FullName;
            cleanupFailure.Data["nx_builder_destroy_exception_message"] = destroyException.Message;
            throw cleanupFailure;
        }

        Dictionary<string, List<string>> helperTagsAfterNative = PartHelperTagInventory(part);
        Dictionary<string, List<string>> helperTagsCreated = HelperTagDifference(helperTagsBefore, helperTagsAfterNative);
        Dictionary<string, List<string>> helperTagsDeleteScheduled = EmptyHelperTagInventory();
        List<TaggedObject> helperObjectsToDelete = new List<TaggedObject>();
        ScheduleHelperDeletion(part.Points.ToArray(), helperTagsBefore["points"], helperTagsDeleteScheduled["points"], helperObjectsToDelete);
        ScheduleHelperDeletion(part.Axes.ToArray(), helperTagsBefore["axes"], helperTagsDeleteScheduled["axes"], helperObjectsToDelete);
        ScheduleHelperDeletion(part.Directions.ToArray(), helperTagsBefore["directions"], helperTagsDeleteScheduled["directions"], helperObjectsToDelete);
        if (helperObjectsToDelete.Count > 0)
        {
            Session.GetSession().UpdateManager.AddObjectsToDeleteList(helperObjectsToDelete.ToArray());
            deleteHelpersScheduled = true;
        }
        evidence["helper_tags_before"] = PublicHelperTagInventory(helperTagsBefore);
        evidence["helper_tags_created"] = PublicHelperTagInventory(helperTagsCreated);
        evidence["helper_tags_delete_scheduled"] = PublicHelperTagInventory(helperTagsDeleteScheduled);

        NXObject[] geometryAfter = sketch.GetAllGeometry();
        List<Curve> outputs = new List<Curve>();
        List<object> outputRecords = new List<object>();
        List<object> unsupportedNewObjects = new List<object>();
        if (geometryAfter == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null after F09 native builder commit");
        foreach (NXObject item in geometryAfter)
        {
            if (item == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null object after F09 builder commit");
            if (beforeTags.Contains(item.Tag.ToString())) continue;
            Curve curve = item as Curve;
            if (curve == null)
            {
                unsupportedNewObjects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", item.Tag.ToString() }, { "type", item.GetType().Name }
                });
                continue;
            }
            if (!sketch.IsSketchGeometry(curve))
                throw new M2ModelException("PATTERN_OUTPUT_INVALID", "new F09 curve Tag is not owned by the target Sketch");
            outputs.Add(curve);
            outputRecords.Add(NativeCurveRecord(curve));
        }
        HashSet<string> globalCurveTagsAfter = PartCurveTags(part);
        List<string> globalCurveTagDelta = TagDifference(globalCurveTagsBefore, globalCurveTagsAfter);
        List<string> sketchOutputTags = new List<string>();
        foreach (Curve output in outputs) sketchOutputTags.Add(output.Tag.ToString());
        sketchOutputTags.Sort(StringComparer.Ordinal);
        bool globalOutputListConsistent = StringSetsEqual(globalCurveTagDelta, sketchOutputTags);
        if (!globalOutputListConsistent)
            throw new M2ModelException("PATTERN_OUTPUT_INVALID", "new Part.Curves Tag delta does not equal the target Sketch output Tag list");
        evidence["native_engine"] = nativeEngine;
        evidence["geometry_only"] = true;
        evidence["associative_pattern_feature_created"] = false;
        evidence["native_builder_used"] = builder != null;
        evidence["global_curve_tag_delta"] = StringListObjects(globalCurveTagDelta);
        evidence["global_output_list_consistent_with_target_sketch"] = globalOutputListConsistent;
        evidence["builder_type"] = builderType;
        evidence["create_constraint"] = createConstraint;
        evidence["create_spacing_expression"] = createSpacingExpression;
        evidence["commit_returned_null"] = committed == null;
        evidence["commit_object_type"] = committed == null ? null : committed.GetType().Name;
        evidence["source_curve_tags"] = SourceTags(sourceCurves);
        evidence["output_curve_count"] = outputs.Count;
        evidence["output_curve_tags"] = OutputTags(outputs);
        evidence["output_curves"] = outputRecords;
        evidence["unsupported_new_sketch_objects"] = unsupportedNewObjects;
        evidence["temporary_rotation_helpers_scheduled_for_delete"] = deleteHelpersScheduled;
        evidence["temporary_rotation_axis_tag"] = null;
        evidence["temporary_rotation_center_tag"] = null;

        return new V2SketchPatternNativeResult
        {
            OutputCurves = outputs.ToArray(),
            CommitObject = committed,
            Evidence = evidence
        };
    }

    private static void AddInputSection(Part part, Section section, Curve[] sourceCurves)
    {
        if (section == null) throw new M2ModelException("PATTERN_INPUT_FAILED", "NX sketch pattern builder returned no input Section");
        section.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        section.AllowSelfIntersection(false);
        foreach (Curve curve in sourceCurves)
        {
            if (curve == null) throw new M2ModelException("PATTERN_INPUT_FAILED", "F09 source curve is null");
            SelectionIntentRule rule = part.ScRuleFactory.CreateRuleCurveDumb(new Curve[] { curve });
            section.AddToSection(new SelectionIntentRule[] { rule }, curve, null, null,
                CurveStartPoint(curve), Section.Mode.Create, false);
        }
    }

    private static Point3d CurveStartPoint(Curve curve)
    {
        Line line = curve as Line;
        if (line != null) return line.StartPoint;
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center;
            Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            double angle = arc.StartAngle;
            return new Point3d(center.X + arc.Radius * (xAxis.X * Math.Cos(angle) + yAxis.X * Math.Sin(angle)),
                center.Y + arc.Radius * (xAxis.Y * Math.Cos(angle) + yAxis.Y * Math.Sin(angle)),
                center.Z + arc.Radius * (xAxis.Z * Math.Cos(angle) + yAxis.Z * Math.Sin(angle)));
        }
        throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F09 native pattern input supports Line and circular Arc only");
    }

    private static Vector3d WorldDirection(Sketch sketch, double[] local, string label)
    {
        if (local == null || local.Length != 2 || !IsFinite(local[0]) || !IsFinite(local[1]))
            throw new M2ModelException("INVALID_REQUEST", label + " must contain two finite coordinates");
        double scale = Math.Max(Math.Abs(local[0]), Math.Abs(local[1]));
        if (!IsFinite(scale) || scale <= 0.0) throw new M2ModelException("INVALID_REQUEST", label + " must be nonzero");
        double x = local[0] / scale, y = local[1] / scale;
        double length = Math.Sqrt(x * x + y * y);
        if (!IsFinite(length) || length <= 0.0) throw new M2ModelException("INVALID_REQUEST", label + " is degenerate");
        x /= length;
        y /= length;
        Matrix3x3 axes = sketch.Orientation.Element;
        Vector3d result = new Vector3d(axes.Xx * x + axes.Yx * y,
            axes.Xy * x + axes.Yy * y, axes.Xz * x + axes.Yz * y);
        Normalize(ref result, label + " world direction");
        return result;
    }

    private static Point3d SketchPointToWorld(Sketch sketch, double x, double y)
    {
        if (!IsFinite(x) || !IsFinite(y)) throw new M2ModelException("INVALID_REQUEST", "center_xy must be finite");
        Matrix3x3 axes = sketch.Orientation.Element;
        Point3d origin = sketch.Origin;
        Point3d result = new Point3d(origin.X + axes.Xx * x + axes.Yx * y,
            origin.Y + axes.Xy * x + axes.Yy * y,
            origin.Z + axes.Xz * x + axes.Yz * y);
        if (!IsFinite(result.X) || !IsFinite(result.Y) || !IsFinite(result.Z))
            throw new M2ModelException("INVALID_REQUEST", "center_xy produces a non-finite world point");
        return result;
    }

    private static Vector3d SketchNormal(Sketch sketch)
    {
        Matrix3x3 axes = sketch.Orientation.Element;
        Vector3d x = new Vector3d(axes.Xx, axes.Xy, axes.Xz);
        Vector3d y = new Vector3d(axes.Yx, axes.Yy, axes.Yz);
        Vector3d normal = new Vector3d(x.Y * y.Z - x.Z * y.Y,
            x.Z * y.X - x.X * y.Z, x.X * y.Y - x.Y * y.X);
        Normalize(ref normal, "sketch normal");
        return normal;
    }

    private static void Normalize(ref Vector3d value, string label)
    {
        double scale = Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        if (!IsFinite(scale) || scale <= 0.0) throw new M2ModelException("INVALID_REQUEST", label + " is zero or non-finite");
        double x = value.X / scale, y = value.Y / scale, z = value.Z / scale;
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (!IsFinite(length) || length <= 0.0) throw new M2ModelException("INVALID_REQUEST", label + " is degenerate");
        value = new Vector3d(x / length, y / length, z / length);
    }

    private static HashSet<string> SketchCurveTags(Sketch sketch)
    {
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null before F09 builder");
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (NXObject item in geometry)
        {
            if (item == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null before F09 builder");
            tags.Add(item.Tag.ToString());
        }
        return tags;
    }

    private static HashSet<string> PartCurveTags(Part part)
    {
        Curve[] curves = part.Curves.ToArray();
        if (curves == null) throw new M2ModelException("MODEL_READ_FAILED", "Part.Curves returned null during F09 pattern inventory capture");
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Curve curve in curves)
        {
            if (curve == null) throw new M2ModelException("MODEL_READ_FAILED", "Part.Curves returned a null curve during F09 pattern inventory capture");
            string tag = curve.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || !tags.Add(tag))
                throw new M2ModelException("MODEL_READ_FAILED", "Part.Curves returned an empty or duplicate Tag during F09 pattern inventory capture");
        }
        return tags;
    }

    private static List<string> TagDifference(HashSet<string> before, HashSet<string> after)
    {
        List<string> result = new List<string>();
        foreach (string tag in after) if (!before.Contains(tag)) result.Add(tag);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static bool StringSetsEqual(List<string> left, List<string> right)
    {
        if (left == null || right == null || left.Count != right.Count) return false;
        HashSet<string> values = new HashSet<string>(left, StringComparer.Ordinal);
        if (values.Count != left.Count) return false;
        foreach (string value in right) if (!values.Remove(value)) return false;
        return values.Count == 0;
    }

    private static Dictionary<string, object> NativeCurveRecord(Curve curve)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record["tag"] = curve.Tag.ToString();
        record["object_type"] = curve.GetType().Name;
        record["length_mm"] = curve.GetLength();
        Line line = curve as Line;
        if (line != null)
        {
            record["start_mm"] = PointRecord(line.StartPoint);
            record["end_mm"] = PointRecord(line.EndPoint);
            return record;
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center;
            Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            record["center_mm"] = PointRecord(arc.CenterPoint);
            record["radius_mm"] = arc.Radius;
            record["start_angle_rad"] = arc.StartAngle;
            record["end_angle_rad"] = arc.EndAngle;
            record["orientation_x"] = VectorRecord(xAxis);
            record["orientation_y"] = VectorRecord(yAxis);
            record["start_mm"] = PointRecord(CurveStartPoint(arc));
            double end = arc.EndAngle;
            record["end_mm"] = PointRecord(ArcPoint(arc, end));
            record["full_circle"] = Math.Abs(arc.GetLength() - 2.0 * Math.PI * arc.Radius) <= 1.0e-6;
        }
        return record;
    }

    private static Point3d ArcPoint(Arc arc, double angle)
    {
        Point3d center;
        Vector3d xAxis, yAxis;
        arc.GetOrientation(out center, out xAxis, out yAxis);
        return new Point3d(center.X + arc.Radius * (xAxis.X * Math.Cos(angle) + yAxis.X * Math.Sin(angle)),
            center.Y + arc.Radius * (xAxis.Y * Math.Cos(angle) + yAxis.Y * Math.Sin(angle)),
            center.Z + arc.Radius * (xAxis.Z * Math.Cos(angle) + yAxis.Z * Math.Sin(angle)));
    }

    private static List<object> SourceTags(Curve[] curves)
    {
        List<object> result = new List<object>();
        foreach (Curve curve in curves) result.Add(curve.Tag.ToString());
        return result;
    }

    private static List<object> OutputTags(List<Curve> curves)
    {
        List<object> result = new List<object>();
        foreach (Curve curve in curves) result.Add(curve.Tag.ToString());
        return result;
    }

    private static Dictionary<string, List<string>> PartHelperTagInventory(Part part)
    {
        Dictionary<string, List<string>> result = EmptyHelperTagInventory();
        foreach (Point point in part.Points.ToArray()) result["points"].Add(point.Tag.ToString());
        foreach (Axis axis in part.Axes.ToArray()) result["axes"].Add(axis.Tag.ToString());
        foreach (Direction direction in part.Directions.ToArray()) result["directions"].Add(direction.Tag.ToString());
        SortHelperTagInventory(result);
        return result;
    }

    private static Dictionary<string, List<string>> EmptyHelperTagInventory()
    {
        return new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            { "points", new List<string>() },
            { "axes", new List<string>() },
            { "directions", new List<string>() }
        };
    }

    private static Dictionary<string, List<string>> HelperTagDifference(
        Dictionary<string, List<string>> before, Dictionary<string, List<string>> after)
    {
        Dictionary<string, List<string>> result = EmptyHelperTagInventory();
        foreach (string kind in result.Keys)
        {
            HashSet<string> retained = new HashSet<string>(before[kind], StringComparer.Ordinal);
            foreach (string tag in after[kind]) if (!retained.Contains(tag)) result[kind].Add(tag);
        }
        SortHelperTagInventory(result);
        return result;
    }

    private static void ScheduleHelperDeletion(TaggedObject[] current,
        List<string> baselineTags, List<string> scheduledTags, List<TaggedObject> deleteObjects)
    {
        if (current == null) throw new M2ModelException("MODEL_READ_FAILED", "NX helper object collection returned null before F09 cleanup");
        HashSet<string> baseline = new HashSet<string>(baselineTags, StringComparer.Ordinal);
        foreach (TaggedObject item in current)
        {
            if (item == null) throw new M2ModelException("MODEL_READ_FAILED", "NX helper object collection contains null before F09 cleanup");
            string tag = item.Tag.ToString();
            if (baseline.Contains(tag)) continue;
            scheduledTags.Add(tag);
            deleteObjects.Add(item);
        }
    }

    private static Dictionary<string, object> PublicHelperTagInventory(Dictionary<string, List<string>> source)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "points", StringListObjects(source["points"]) },
            { "axes", StringListObjects(source["axes"]) },
            { "directions", StringListObjects(source["directions"]) }
        };
    }

    private static void SortHelperTagInventory(Dictionary<string, List<string>> inventory)
    {
        foreach (List<string> tags in inventory.Values)
        {
            tags.Sort(StringComparer.Ordinal);
            for (int index = tags.Count - 1; index > 0; index--)
                if (String.Equals(tags[index], tags[index - 1], StringComparison.Ordinal)) tags.RemoveAt(index);
        }
    }

    private static List<object> StringListObjects(List<string> values)
    {
        List<object> result = new List<object>();
        foreach (string value in values) result.Add(value);
        return result;
    }

    private static List<object> PointRecord(Point3d point)
    {
        return new List<object> { NumberText(point.X), NumberText(point.Y), NumberText(point.Z) };
    }

    private static List<object> VectorRecord(Vector3d vector)
    {
        return new List<object> { NumberText(vector.X), NumberText(vector.Y), NumberText(vector.Z) };
    }

    private static string NumberText(double value)
    {
        if (!IsFinite(value)) throw new M2ModelException("INVALID_REQUEST", "pattern parameter must be finite");
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool IsFinite(double value)
    {
        return !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
}

public sealed class V2SketchPatternNativeResult
{
    public Curve[] OutputCurves;
    public NXObject CommitObject;
    public Dictionary<string, object> Evidence;
}
