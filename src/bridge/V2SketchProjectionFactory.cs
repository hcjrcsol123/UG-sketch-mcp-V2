using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using NXOpen;
using NxSketchMcp.M2;

/// <summary>Native result from one NX sketch projection operation.</summary>
public sealed class V2SketchProjectionNativeResult
{
    public NXObject CommitObject;
    public NXObject[] CommittedObjects;
    public Curve[] OutputCurves;
    public Dictionary<string, object> Evidence;
}

/// <summary>Creates native sketch projections using NX's SketchProjectBuilder.</summary>
public static class V2SketchProjectionFactory
{
    /// <summary>
    /// Projects native curves and edges into the already active target sketch. The caller
    /// owns sketch activation/deactivation and restricts projection direction to the target
    /// sketch normal or its negative because SketchProjectBuilder has no direction vector.
    /// </summary>
    public static V2SketchProjectionNativeResult Execute(Part part, Sketch targetSketch,
        NXObject[] sourceObjects, bool associativity)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (targetSketch == null) throw new ArgumentNullException("targetSketch");
        if (sourceObjects == null) throw new ArgumentNullException("sourceObjects");
        if (sourceObjects.Length == 0)
            throw new ArgumentException("At least one native Edge or Curve is required.", "sourceObjects");

        BasePart targetPart = targetSketch.OwningPart;
        if (targetPart == null || !targetPart.Tag.Equals(part.Tag))
            throw new ArgumentException("targetSketch must belong to the supplied Part.", "targetSketch");

        NXObject[] sources = (NXObject[])sourceObjects.Clone();
        HashSet<string> sourceTags = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sources.Length; i++)
        {
            NXObject source = sources[i];
            if (source == null)
                throw new ArgumentException("sourceObjects cannot contain null entries.", "sourceObjects");
            if (!(source is Edge) && !(source is Curve))
                throw new ArgumentException("Every source must be a native Edge or Curve.", "sourceObjects");

            BasePart sourcePart = source.OwningPart;
            if (sourcePart == null || !sourcePart.Tag.Equals(part.Tag))
                throw new ArgumentException("Every source Edge or Curve must belong to the supplied Part.", "sourceObjects");

            string tag = source.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || !sourceTags.Add(tag))
                throw new ArgumentException("sourceObjects must have non-empty, unique Tags.", "sourceObjects");
        }

        Dictionary<string, NXObject> beforeGeometry = SketchGeometryByTag(targetSketch,
            "before native sketch projection");

        SketchProjectBuilder builder = null;
        V2SketchProjectionNativeResult result = null;
        Exception operationException = null;
        try
        {
            builder = part.Sketches.CreateProjectBuilder(null);
            if (builder == null)
                throw new M2ModelException("PROJECTION_BUILDER_INVALID",
                    "NX returned no SketchProjectBuilder for native sketch projection");

            builder.Associativity = associativity;
            builder.CurveType = SketchProjectBuilder.OutputCurve.Original;
            builder.ProjectAsDumbFixedCurves = false;
            bool associativityReadback = builder.Associativity;
            builder.Tolerance = 0.001;
            string curveType = builder.CurveType.ToString();
            bool projectAsDumbFixedCurves = builder.ProjectAsDumbFixedCurves;
            double builderTolerance = builder.Tolerance;
            if (associativityReadback != associativity || projectAsDumbFixedCurves)
                throw new M2ModelException("PROJECTION_BUILDER_OPTIONS_INVALID",
                    "NX did not retain the requested Associativity and ProjectAsDumbFixedCurves=false builder options");

            Section section = builder.Section;
            if (section == null)
                throw new M2ModelException("PROJECTION_INPUT_FAILED",
                    "NX SketchProjectBuilder returned no input Section");
            section.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
            section.DistanceTolerance = 0.001;
            section.ChainingTolerance = 0.001;
            section.AngleTolerance = 0.5;
            double sectionDistanceTolerance = section.DistanceTolerance;
            double sectionChainingTolerance = section.ChainingTolerance;
            double sectionAngleTolerance = section.AngleTolerance;

            for (int i = 0; i < sources.Length; i++)
                AddSourceToSection(part, section, sources[i]);

            NXObject commitObject = builder.CommitOperation();
            NXObject[] builderCommittedObjects = builder.GetCommittedObjects();
            NXObject[] committedObjects = MergeCommittedObjects(commitObject, builderCommittedObjects);

            Dictionary<string, NXObject> afterGeometry = SketchGeometryByTag(targetSketch,
                "after native sketch projection commit");
            List<Curve> outputCurves = new List<Curve>();
            List<object> outputCurveTags = new List<object>();
            List<object> outputCurveTypes = new List<object>();
            List<object> unsupportedNewObjects = new List<object>();
            foreach (KeyValuePair<string, NXObject> item in afterGeometry)
            {
                if (beforeGeometry.ContainsKey(item.Key)) continue;

                Curve curve = item.Value as Curve;
                if (curve == null)
                {
                    unsupportedNewObjects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "tag", item.Key },
                        { "object_type", item.Value.GetType().Name }
                    });
                    continue;
                }
                if (!targetSketch.IsSketchGeometry(curve))
                    throw new M2ModelException("PROJECTION_OUTPUT_INVALID",
                        "A new projection curve Tag is not owned by the target Sketch");

                outputCurves.Add(curve);
                outputCurveTags.Add(item.Key);
                outputCurveTypes.Add(curve.GetType().Name);
            }

            if (outputCurves.Count == 0)
                throw new M2ModelException("PROJECTION_OUTPUT_INVALID",
                    "NX sketch projection commit produced no new target Sketch curves");

            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
            evidence["native_engine"] = "NXOpen.SketchProjectBuilder";
            evidence["target_sketch_tag"] = targetSketch.Tag.ToString();
            evidence["source_count"] = sources.Length;
            evidence["source_tags"] = SourceTags(sources);
            evidence["source_object_types"] = SourceTypes(sources);
            evidence["associativity_requested"] = associativity;
            evidence["associativity_readback"] = associativityReadback;
            evidence["curve_type"] = curveType;
            evidence["project_as_dumb_fixed_curves"] = projectAsDumbFixedCurves;
            evidence["builder_tolerance"] = builderTolerance;
            evidence["section_distance_tolerance"] = sectionDistanceTolerance;
            evidence["section_chaining_tolerance"] = sectionChainingTolerance;
            evidence["section_angle_tolerance_api"] = sectionAngleTolerance;
            evidence["commit_method"] = "CommitOperation";
            evidence["commit_returned_null"] = commitObject == null;
            evidence["commit_object_type"] = commitObject == null ? null : commitObject.GetType().Name;
            evidence["committed_object_count"] = committedObjects.Length;
            evidence["committed_object_types"] = ObjectTypes(committedObjects);
            evidence["output_curve_count"] = outputCurves.Count;
            evidence["output_curve_tags"] = outputCurveTags;
            evidence["output_curve_types"] = outputCurveTypes;
            evidence["unsupported_new_sketch_objects"] = unsupportedNewObjects;

            result = new V2SketchProjectionNativeResult
            {
                CommitObject = commitObject,
                CommittedObjects = committedObjects,
                OutputCurves = outputCurves.ToArray(),
                Evidence = evidence
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

        // Preserve the native operation failure while still attempting builder cleanup.
        if (operationException != null)
            ExceptionDispatchInfo.Capture(operationException).Throw();
        if (destroyException != null)
            throw new InvalidOperationException(
                "NX sketch projection completed but its builder could not be destroyed.", destroyException);
        return result;
    }

    private static void AddSourceToSection(Part part, Section section, NXObject source)
    {
        Edge edge = source as Edge;
        if (edge != null)
        {
            SelectionIntentRule edgeRule = part.ScRuleFactory.CreateRuleEdgeDumb(new Edge[] { edge });
            if (edgeRule == null)
                throw new M2ModelException("PROJECTION_INPUT_FAILED",
                    "NX returned no dumb selection rule for a source Edge");
            section.AddToSection(new SelectionIntentRule[] { edgeRule }, edge, null, null,
                SourceHelpPoint(edge), Section.Mode.Create, false);
            return;
        }

        Curve curve = source as Curve;
        if (curve != null)
        {
            SelectionIntentRule curveRule = part.ScRuleFactory.CreateRuleCurveDumb(new Curve[] { curve });
            if (curveRule == null)
                throw new M2ModelException("PROJECTION_INPUT_FAILED",
                    "NX returned no dumb selection rule for a source Curve");
            section.AddToSection(new SelectionIntentRule[] { curveRule }, curve, null, null,
                SourceHelpPoint(curve), Section.Mode.Create, false);
            return;
        }

        throw new M2ModelException("PROJECTION_INPUT_FAILED",
            "Native sketch projection supports Edge and Curve source objects only");
    }

    private static Point3d SourceHelpPoint(NXObject source)
    {
        Edge edge = source as Edge;
        if (edge != null)
        {
            Point3d first, second;
            edge.GetVertices(out first, out second);
            return first;
        }

        Line line = source as Line;
        if (line != null) return line.StartPoint;

        Arc arc = source as Arc;
        if (arc != null)
        {
            Point3d center;
            Vector3d axisX, axisY;
            arc.GetOrientation(out center, out axisX, out axisY);
            double angle = arc.StartAngle;
            return new Point3d(
                center.X + arc.Radius * (axisX.X * Math.Cos(angle) + axisY.X * Math.Sin(angle)),
                center.Y + arc.Radius * (axisX.Y * Math.Cos(angle) + axisY.Y * Math.Sin(angle)),
                center.Z + arc.Radius * (axisX.Z * Math.Cos(angle) + axisY.Z * Math.Sin(angle)));
        }

        throw new M2ModelException("UNSUPPORTED_CURVE_TYPE",
            "F10 supports source Line and circular Arc curves or linear and circular solid Edges only");
    }

    private static Dictionary<string, NXObject> SketchGeometryByTag(Sketch sketch, string stage)
    {
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null)
            throw new M2ModelException("MODEL_READ_FAILED",
                "Sketch.GetAllGeometry returned null " + stage);

        Dictionary<string, NXObject> result = new Dictionary<string, NXObject>(StringComparer.Ordinal);
        for (int i = 0; i < geometry.Length; i++)
        {
            NXObject item = geometry[i];
            if (item == null)
                throw new M2ModelException("MODEL_READ_FAILED",
                    "Sketch.GetAllGeometry returned a null object " + stage);
            string tag = item.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || result.ContainsKey(tag))
                throw new M2ModelException("MODEL_READ_FAILED",
                    "Sketch.GetAllGeometry returned an empty or duplicate Tag " + stage);
            result.Add(tag, item);
        }
        return result;
    }

    private static NXObject[] MergeCommittedObjects(NXObject commitObject, NXObject[] committedObjects)
    {
        List<NXObject> result = new List<NXObject>();
        if (committedObjects != null)
            for (int i = 0; i < committedObjects.Length; i++) AddUnique(result, committedObjects[i]);
        AddUnique(result, commitObject);
        return result.ToArray();
    }

    private static void AddUnique(List<NXObject> objects, NXObject candidate)
    {
        if (candidate == null) return;
        for (int i = 0; i < objects.Count; i++)
            if (objects[i].Tag.Equals(candidate.Tag)) return;
        objects.Add(candidate);
    }

    private static List<object> SourceTags(NXObject[] objects)
    {
        List<object> result = new List<object>();
        for (int i = 0; i < objects.Length; i++) result.Add(objects[i].Tag.ToString());
        return result;
    }

    private static List<object> SourceTypes(NXObject[] objects)
    {
        List<object> result = new List<object>();
        for (int i = 0; i < objects.Length; i++) result.Add(objects[i].GetType().Name);
        return result;
    }

    private static List<object> ObjectTypes(NXObject[] objects)
    {
        List<object> result = new List<object>();
        for (int i = 0; i < objects.Length; i++)
            result.Add(objects[i] == null ? null : objects[i].GetType().Name);
        return result;
    }
}
