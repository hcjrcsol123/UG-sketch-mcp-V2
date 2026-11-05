using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NXOpen.GeometricUtilities;

// Native feature-pattern primitives. The caller owns transaction boundaries and
// must verify geometry, preservation and the builder-read side effects.
public static class V2FeaturePatternFactory
{
    public sealed class ReadStateUnknownException : Exception
    {
        public ReadStateUnknownException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class Definition
    {
        public string Mode;
        public int Count1, Count2;
        public double Pitch1, Pitch2, AngleDeg;
        public double[] Direction1, Direction2, AxisOrigin, AxisDirection;
    }

    private static string Formula(double value)
    { return value.ToString("R", CultureInfo.InvariantCulture); }

    private static Point3d Point(double[] a)
    { return new Point3d(a[0], a[1], a[2]); }

    private static Vector3d Vector(double[] a)
    { return new Vector3d(a[0], a[1], a[2]); }

    public static PatternFeature Create(Part part, Feature seed, Definition d)
    {
        if (part == null || seed == null || d == null || seed.OwningPart == null ||
            !part.Tag.Equals(seed.OwningPart.Tag)) throw new ArgumentException("A seed feature in the work part is required.");
        PatternFeatureBuilder b = null;
        try
        {
            b = part.Features.CreatePatternFeatureBuilder(null);
            if (b == null || !b.FeatureList.Add(new Feature[] { seed }))
                throw new InvalidOperationException("NX rejected the seed feature.");
            b.PatternMethod = PatternFeatureBuilder.PatternMethodOptions.Simple;
            b.OutputOption = PatternFeatureBuilder.OutputOptions.PatternFeature;
            b.ExpressionOption = PatternFeatureBuilder.ExpressionTransferOptions.CreateNew;
            if (d.Mode == "linear" || d.Mode == "rectangular")
            {
                b.PatternService.PatternType = PatternDefinition.PatternEnum.Linear;
                b.PatternService.PatternOrientation.LinearOrientationOption = PatternOrientation.Enum.Fixed;
                RectangularPattern r = b.PatternService.RectangularDefinition;
                r.UseYDirectionToggle = d.Mode == "rectangular";
                r.XSpacing.NCopies.RightHandSide = d.Count1.ToString(CultureInfo.InvariantCulture);
                r.XSpacing.PitchDistance.RightHandSide = Formula(d.Pitch1);
                r.XDirection = part.Directions.CreateDirection(new Point3d(0, 0, 0), Vector(d.Direction1), SmartObject.UpdateOption.WithinModeling);
                if (r.UseYDirectionToggle)
                {
                    r.YSpacing.NCopies.RightHandSide = d.Count2.ToString(CultureInfo.InvariantCulture);
                    r.YSpacing.PitchDistance.RightHandSide = Formula(d.Pitch2);
                    r.YDirection = part.Directions.CreateDirection(new Point3d(0, 0, 0), Vector(d.Direction2), SmartObject.UpdateOption.WithinModeling);
                }
            }
            else if (d.Mode == "circular")
            {
                b.PatternService.PatternType = PatternDefinition.PatternEnum.Circular;
                b.PatternService.PatternOrientation.CircularOrientationOption = PatternOrientation.Enum.FollowPattern;
                CircularPattern c = b.PatternService.CircularDefinition;
                c.AngularSpacing.NCopies.RightHandSide = d.Count1.ToString(CultureInfo.InvariantCulture);
                c.AngularSpacing.PitchAngle.RightHandSide = Formula(d.AngleDeg);
                Point p = part.Points.CreatePoint(Point(d.AxisOrigin));
                Direction direction = part.Directions.CreateDirection(Point(d.AxisOrigin), Vector(d.AxisDirection), SmartObject.UpdateOption.WithinModeling);
                c.RotationAxis = part.Axes.CreateAxis(p, direction, SmartObject.UpdateOption.WithinModeling);
            }
            else throw new ArgumentException("Unsupported pattern mode.");
            if (!b.Validate()) throw new InvalidOperationException("NX rejected the pattern definition.");
            PatternFeature feature = b.CommitFeature() as PatternFeature;
            if (feature == null) throw new InvalidOperationException("NX did not return a PatternFeature.");
            return feature;
        }
        finally
        {
            if (b != null)
            {
                try { b.Destroy(); }
                catch (Exception ex) { throw new ReadStateUnknownException("Pattern builder destruction failed.", ex); }
            }
        }
    }

    // Returns actual instance/output containment, rather than inferring it from
    // creation order or journal identifier spelling. No builder is opened here.
    public static List<Dictionary<string, object>> Instances(PatternFeature pattern, Feature seed)
    {
        if (pattern == null || seed == null) throw new ArgumentException("Pattern and seed are required.");
        Feature[] immediate = pattern.GetImmediateContainedFeatures();
        Body[] associated = pattern.GetAssociatedBodies();
        if (immediate == null || associated == null) throw new InvalidOperationException("NX did not return pattern containment.");
        HashSet<Tag> expected = new HashSet<Tag>();
        foreach (Body body in associated)
            if (body == null || !expected.Add(body.Tag)) throw new InvalidOperationException("Pattern associated bodies are invalid or repeated.");
        HashSet<Tag> actual = new HashSet<Tag>();
        List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
        foreach (Feature item in immediate)
        {
            InstanceFeature instance = item as InstanceFeature;
            Feature parentContainer = instance == null ? null : instance.ContainerFeature as Feature;
            if (instance == null || parentContainer == null || !parentContainer.Tag.Equals(pattern.Tag) || !HasParent(instance, seed)) throw new InvalidOperationException("Unsupported instance containment or seed association.");
            Feature[] contained = instance.GetAllContainedFeatures();
            Feature outputContainer = contained == null || contained.Length != 1 || contained[0] == null ? null : contained[0].ContainerFeature as Feature;
            if (contained == null || contained.Length != 1 || contained[0] == null ||
                !String.Equals(contained[0].FeatureType, "IDENTICAL INSTANCE OUTPUT", StringComparison.Ordinal) || outputContainer == null ||
                !outputContainer.Tag.Equals(instance.Tag) || !HasParent(contained[0], seed))
                throw new InvalidOperationException("An instance does not own exactly one supported output.");
            Body[] bodies = contained[0].GetBodies();
            if (bodies == null || bodies.Length != 1 || bodies[0] == null || !bodies[0].IsSolidBody || !actual.Add(bodies[0].Tag))
                throw new InvalidOperationException("An instance does not own one unique solid body.");
            result.Add(new Dictionary<string, object> { { "instance_id", instance.JournalIdentifier },
                { "output_feature_id", contained[0].JournalIdentifier }, { "body_id", bodies[0].JournalIdentifier },
                { "instance_tag", instance.Tag.ToString() }, { "output_feature_tag", contained[0].Tag.ToString() },
                { "body_tag", bodies[0].Tag.ToString() } });
        }
        if (!expected.SetEquals(actual)) throw new InvalidOperationException("Associated bodies and instance outputs differ.");
        return result;
    }

    private static bool HasParent(Feature feature, Feature expected)
    {
        Feature[] parents = feature.GetParents();
        if (parents == null) return false;
        foreach (Feature parent in parents) if (parent != null && parent.Tag.Equals(expected.Tag)) return true;
        return false;
    }

    private static double[] Components(Vector3d v)
    { return new double[] { v.X, v.Y, v.Z }; }
    private static double[] Components(Point3d p)
    { return new double[] { p.X, p.Y, p.Z }; }
    private static Dictionary<string, object> ExpressionData(Expression expression)
    {
        if (expression == null) throw new InvalidOperationException("Native pattern expression is missing.");
        return new Dictionary<string, object> { { "rhs", expression.RightHandSide }, { "value", expression.Value },
            { "units", expression.Units == null ? null : expression.Units.JournalIdentifier } };
    }

    // This method must run only inside the read-only inner undo transaction.
    // Every returned item is materialized; no NX object escapes the boundary.
    private static Dictionary<string, object> ReadMaterialized(Part part, PatternFeature pattern)
    {
        PatternFeatureBuilder b = null;
        try
        {
            b = part.Features.CreatePatternFeatureBuilder(pattern);
            if (b == null) throw new InvalidOperationException("NX did not open the pattern builder.");
            TaggedObject[] selected = b.FeatureList.GetArray();
            if (selected == null || selected.Length != 1 || !(selected[0] is Feature))
                throw new InvalidOperationException("The pattern must have one explicit feature seed.");
            Feature seed = (Feature)selected[0];
            if (!HasParent(pattern, seed)) throw new InvalidOperationException("Pattern does not identify its seed as a parent.");
            Dictionary<string, object> result = new Dictionary<string, object> {
                { "feature_id", pattern.JournalIdentifier }, { "seed_feature_id", seed.JournalIdentifier },
                { "method", b.PatternMethod.ToString() }, { "output_option", b.OutputOption.ToString() },
                { "expression_option", b.ExpressionOption.ToString() },
                { "pattern_type", b.PatternService.PatternType.ToString() },
                { "seed_only", b.PatternService.SeedOnlyToggle }, { "frame_only", b.PatternService.FrameOnlyToggle },
                { "reference_point_tag", b.ReferencePointService.Point == null ? null : b.ReferencePointService.Point.Tag.ToString() },
                { "reference_point_mm", b.ReferencePointService.Point == null ? null : Components(b.ReferencePointService.Point.Coordinates) },
                { "reference_inferred", b.ReferencePointService.IsReferencePointInferred },
                { "instances", Instances(pattern, seed) }
            };
            if (b.PatternService.PatternType == PatternDefinition.PatternEnum.Linear)
            {
                RectangularPattern r = b.PatternService.RectangularDefinition;
                if (r.XDirection == null || (r.UseYDirectionToggle && r.YDirection == null))
                    throw new InvalidOperationException("Pattern direction is missing.");
                result.Add("use_y", r.UseYDirectionToggle);
                result.Add("orientation", b.PatternService.PatternOrientation.LinearOrientationOption.ToString());
                result.Add("x_count", r.XSpacing.NCopies.Value); result.Add("x_count_rhs", r.XSpacing.NCopies.RightHandSide);
                result.Add("x_pitch", r.XSpacing.PitchDistance.Value); result.Add("x_pitch_rhs", r.XSpacing.PitchDistance.RightHandSide);
                result.Add("x_count_expression", ExpressionData(r.XSpacing.NCopies)); result.Add("x_pitch_expression", ExpressionData(r.XSpacing.PitchDistance));
                result.Add("x_space_type", r.XSpacing.SpaceType.ToString()); result.Add("x_flip", r.XFlip);
                result.Add("x_symmetry", r.XSymmetryToggle); result.Add("x_direction", Components(r.XDirection.Vector));
                result.Add("x_direction_tag", r.XDirection.Tag.ToString());
                result.Add("stagger", r.StaggerType.ToString());
                if (r.UseYDirectionToggle)
                {
                    result.Add("y_count", r.YSpacing.NCopies.Value); result.Add("y_count_rhs", r.YSpacing.NCopies.RightHandSide);
                    result.Add("y_pitch", r.YSpacing.PitchDistance.Value); result.Add("y_pitch_rhs", r.YSpacing.PitchDistance.RightHandSide);
                    result.Add("y_count_expression", ExpressionData(r.YSpacing.NCopies)); result.Add("y_pitch_expression", ExpressionData(r.YSpacing.PitchDistance));
                    result.Add("y_space_type", r.YSpacing.SpaceType.ToString()); result.Add("y_flip", r.YFlip);
                    result.Add("y_symmetry", r.YSymmetryToggle); result.Add("y_direction", Components(r.YDirection.Vector));
                    result.Add("y_direction_tag", r.YDirection.Tag.ToString());
                }
            }
            else if (b.PatternService.PatternType == PatternDefinition.PatternEnum.Circular)
            {
                CircularPattern c = b.PatternService.CircularDefinition;
                if (c.RotationAxis == null || c.RotationAxis.Point == null || c.RotationAxis.Direction == null)
                    throw new InvalidOperationException("Pattern rotation axis is missing.");
                result.Add("count", c.AngularSpacing.NCopies.Value); result.Add("count_rhs", c.AngularSpacing.NCopies.RightHandSide);
                result.Add("angle", c.AngularSpacing.PitchAngle.Value); result.Add("angle_rhs", c.AngularSpacing.PitchAngle.RightHandSide);
                result.Add("count_expression", ExpressionData(c.AngularSpacing.NCopies)); result.Add("angle_expression", ExpressionData(c.AngularSpacing.PitchAngle));
                result.Add("orientation", b.PatternService.PatternOrientation.CircularOrientationOption.ToString());
                result.Add("space_type", c.AngularSpacing.SpaceType.ToString()); result.Add("pitch_option", c.AngularSpacing.UsePitchOption.ToString());
                result.Add("axis_origin", Components(c.RotationAxis.Point.Coordinates)); result.Add("axis_direction", Components(c.RotationAxis.Direction.Vector));
                result.Add("axis_tag", c.RotationAxis.Tag.ToString()); result.Add("axis_point_tag", c.RotationAxis.Point.Tag.ToString());
                result.Add("axis_direction_tag", c.RotationAxis.Direction.Tag.ToString());
                result.Add("include_seed", c.IncludeSeedToggle); result.Add("flip", c.Flip);
                result.Add("use_radial", c.UseRadialDirectionToggle); result.Add("stagger", c.StaggerType.ToString());
            }
            else throw new InvalidOperationException("Unsupported native pattern type.");
            return result;
        }
        finally
        {
            if (b != null)
            {
                try { b.Destroy(); }
                catch (Exception ex) { throw new ReadStateUnknownException("Pattern builder destruction failed.", ex); }
            }
        }
    }

    // The caller supplies a comprehensive, deterministic inventory of the
    // currently edited part, including all expression values/identities, helpers,
    // history and body geometry. A failure in cleanup or comparison is UNKNOWN.
    public static Dictionary<string, object> ReadIsolated(Session session, Part part, PatternFeature pattern, Func<string> inventory)
    {
        if (session == null || part == null || pattern == null || inventory == null)
            throw new ArgumentException("Pattern reader requires session, part, feature and inventory.");
        return ReadIsolatedCore(session,part,inventory,delegate {return ReadMaterialized(part,pattern);});
    }

    public static Dictionary<string, object> ReadSeedIsolated(Session session, Part part, Feature seed, Func<string> inventory)
    {
        if(session==null || part==null || seed==null || inventory==null)throw new ArgumentException("Seed reader inputs missing.");
        return ReadIsolatedCore(session,part,inventory,delegate {
            if(seed is Extrude)
            {
                ExtrudeBuilder b=null;
                try {b=part.Features.CreateExtrudeBuilder(seed);return new Dictionary<string,object>{{"boolean",b.BooleanOperation.Type.ToString()}};}
                finally {if(b!=null)try{b.Destroy();}catch(Exception ex){throw new ReadStateUnknownException("Extrude builder destruction failed.",ex);}}
            }
            if(seed is Block)
            {
                BlockFeatureBuilder b=null;
                try {b=part.Features.CreateBlockFeatureBuilder(seed);return new Dictionary<string,object>{{"boolean",b.BooleanType.ToString()}};}
                finally {if(b!=null)try{b.Destroy();}catch(Exception ex){throw new ReadStateUnknownException("Block builder destruction failed.",ex);}}
            }
            throw new InvalidOperationException("Unsupported seed feature type.");
        });
    }

    private static Dictionary<string, object> ReadIsolatedCore(Session session,Part part,Func<string> inventory,Func<Dictionary<string,object>> read)
    {
        bool dirty = part.IsModified;
        string before;
        try
        {
            before = inventory();
            if (part.IsModified != dirty) throw new InvalidOperationException("Reader inventory changed dirty state.");
        }
        catch (Exception ex) { throw new ReadStateUnknownException("Pattern reader could not establish a side-effect-free initial inventory.", ex); }
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F18 isolated pattern reader");
        Dictionary<string, object> data = null;
        Exception readError = null;
        try { data = read(); }
        catch (Exception ex) { readError = ex; }
        try
        {
            // NXOpen's .NET UndoToMarkWithStatus returns void; exceptions and the
            // subsequent update/state audit provide the available failure signals.
            session.UndoToMarkWithStatus(mark, "F18 isolated pattern reader");
            int update = session.UpdateManager.DoUpdate(mark);
            if (update != 0) throw new InvalidOperationException("NX update returned " + update.ToString(CultureInfo.InvariantCulture));
            session.DeleteUndoMark(mark, "F18 isolated pattern reader");
            if (part.IsModified != dirty) throw new InvalidOperationException("Pattern read changed dirty state.");
            string after = inventory();
            if (part.IsModified != dirty || !String.Equals(before, after, StringComparison.Ordinal))
                throw new InvalidOperationException("Pattern read did not restore the captured part state.");
        }
        catch (Exception ex) { throw new ReadStateUnknownException("Pattern reader could not prove state restoration.", ex); }
        if (readError is ReadStateUnknownException) throw readError;
        if (readError != null) throw new InvalidOperationException("Native pattern is not supported by the reader.", readError);
        return data;
    }
}
