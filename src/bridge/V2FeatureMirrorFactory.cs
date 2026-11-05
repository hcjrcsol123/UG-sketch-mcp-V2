using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;

// The caller owns the outer transaction, geometry verification and inventory.
public static class V2FeatureMirrorFactory
{
    public sealed class ReadStateUnknownException : Exception
    {
        public ReadStateUnknownException(string message, Exception inner) : base(message, inner) { }
    }

    private static bool InPart(Part part, NXObject item)
    { return item != null && !item.IsOccurrence && item.OwningPart != null && part.Tag.Equals(item.OwningPart.Tag); }

    private static bool Finite(double x)
    { return !Double.IsNaN(x) && !Double.IsInfinity(x); }

    private static void ValidatePlane(double[] origin, double[] normal)
    {
        if (origin == null || normal == null || origin.Length != 3 || normal.Length != 3)
            throw new ArgumentException("A three-component origin and normal are required.");
        foreach (double v in origin) if (!Finite(v)) throw new ArgumentException("Plane origin must be finite.");
        foreach (double v in normal) if (!Finite(v)) throw new ArgumentException("Plane normal must be finite.");
        // Unit vectors have bounded components; guard before squaring to avoid overflow.
        foreach (double v in normal) if (Math.Abs(v) > 1.000000001) throw new ArgumentException("Plane normal must be unit length.");
        double length = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
        if (Math.Abs(length - 1.0) > 1e-9) throw new ArgumentException("Plane normal must be unit length within 1e-9.");
    }

    private static double[] Components(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static double[] Components(Vector3d v) { return new double[] { v.X, v.Y, v.Z }; }
    private static string Id(NXObject item, string label)
    {
        if (item == null || String.IsNullOrEmpty(item.JournalIdentifier))
            throw new InvalidOperationException(label + " has no journal identifier.");
        return item.JournalIdentifier;
    }

    public static MirrorFeature Create(Part part, Feature seed, double[] origin, double[] normal, DatumPlaneFeature datumFeature)
    {
        if (part == null || !InPart(part, seed)) throw new ArgumentException("A seed feature in the work part is required.");
        if (datumFeature == null) ValidatePlane(origin, normal);
        else if (origin != null || normal != null || !InPart(part, datumFeature) || datumFeature.DatumPlane == null || !InPart(part, datumFeature.DatumPlane))
            throw new ArgumentException("An exclusive, native work-part datum plane is required.");
        MirrorFeatureBuilder b = null;
        try
        {
            b = part.Features.CreateMirrorFeatureBuilder(null);
            if (b == null || !b.FeatureSet.Add(new Feature[] { seed })) throw new InvalidOperationException("NX rejected the mirror seed.");
            if (datumFeature == null)
            {
                b.PlaneOption = MirrorFeatureBuilder.PlaneOptions.New;
                b.PlaneConstructor = part.Planes.CreatePlane(new Point3d(origin[0], origin[1], origin[2]),
                    new Vector3d(normal[0], normal[1], normal[2]), SmartObject.UpdateOption.WithinModeling);
                if (b.PlaneConstructor == null) throw new InvalidOperationException("NX did not create the mirror plane.");
            }
            else
            {
                b.PlaneOption = MirrorFeatureBuilder.PlaneOptions.Existing;
                b.Plane.Value = datumFeature.DatumPlane;
            }
            if (!b.Validate()) throw new InvalidOperationException("NX rejected the mirror definition.");
            MirrorFeature result = b.CommitFeature() as MirrorFeature;
            if (result == null) throw new InvalidOperationException("NX did not return a MirrorFeature.");
            return result;
        }
        finally
        {
            if (b != null) try { b.Destroy(); }
            catch (Exception ex) { throw new ReadStateUnknownException("Mirror builder destruction failed.", ex); }
        }
    }

    // No NX object escapes this method. The builder is opened only within the inner undo mark.
    private static Dictionary<string, object> ReadMaterialized(Part part, MirrorFeature mirror)
    {
        MirrorFeatureBuilder b = null;
        try
        {
            if (!InPart(part, mirror) || !String.Equals(mirror.FeatureType, "MIRROR_SET", StringComparison.Ordinal))
                throw new InvalidOperationException("A native work-part MIRROR_SET is required.");
            b = part.Features.CreateMirrorFeatureBuilder(mirror);
            if (b == null) throw new InvalidOperationException("NX did not open the mirror builder.");
            TaggedObject[] selected = b.FeatureSet.GetArray();
            Feature seed = selected == null || selected.Length != 1 ? null : selected[0] as Feature;
            if (!InPart(part, seed)) throw new InvalidOperationException("Mirror must select exactly one native work-part seed.");
            DatumPlaneFeature datumFeature = null;
            double[] origin, normal;
            string mode;
            if (b.PlaneOption == MirrorFeatureBuilder.PlaneOptions.New)
            {
                Plane plane = b.PlaneConstructor;
                // NX2506 CreatePlane(origin, normal, WithinModeling) reads back
                // as Undefined (0), with explicit origin/normal and no references.
                // Fixed is also accepted, but no associative geometry may enter
                // this numeric mode.
                if (plane == null || (plane.Method != PlaneTypes.MethodType.Fixed && plane.Method != PlaneTypes.MethodType.Undefined))
                    throw new InvalidOperationException("Mirror requires a fixed or explicitly defined mathematical plane.");
                NXObject[] geometry = plane.GetGeometry();
                if (geometry == null || geometry.Length != 0) throw new InvalidOperationException("Mathematical mirror plane must have no geometry references.");
                origin = Components(plane.Origin); normal = Components(plane.Normal); mode = "numeric";
            }
            else if (b.PlaneOption == MirrorFeatureBuilder.PlaneOptions.Existing)
            {
                DatumPlane datum = b.Plane.Value as DatumPlane;
                datumFeature = datum == null ? null : datum.Feature as DatumPlaneFeature;
                if (!InPart(part, datum) || !InPart(part, datumFeature) || datumFeature.DatumPlane == null ||
                    !datumFeature.DatumPlane.Tag.Equals(datum.Tag))
                    throw new InvalidOperationException("Mirror reference is not a native work-part datum feature.");
                origin = Components(datum.Origin); normal = Components(datum.Normal); mode = "datum";
            }
            else throw new InvalidOperationException("Unsupported mirror plane option.");
            ValidatePlane(origin, normal);
            Feature[] parents = mirror.GetParents();
            if (parents == null || parents.Length != (datumFeature == null ? 1 : 2))
                throw new InvalidOperationException("Unexpected mirror parent features.");
            bool hasSeed = false, hasDatum = datumFeature == null;
            foreach (Feature parent in parents)
            {
                if (parent == null) throw new InvalidOperationException("Null mirror parent.");
                if (parent.Tag.Equals(seed.Tag) && !hasSeed) hasSeed = true;
                else if (datumFeature != null && parent.Tag.Equals(datumFeature.Tag) && !hasDatum) hasDatum = true;
                else throw new InvalidOperationException("Unexpected or repeated mirror parent.");
            }
            if (!hasSeed || !hasDatum) throw new InvalidOperationException("Mirror seed or datum parent does not match the builder.");
            Body[] bodies = mirror.GetBodies();
            if (bodies == null || bodies.Length != 1 || !InPart(part, bodies[0]) || !bodies[0].IsSolidBody)
                throw new InvalidOperationException("Mirror must return exactly one native solid body.");
            Body body = bodies[0];
            Feature output = null;
            foreach (Feature candidate in part.Features.GetFeatures())
            {
                if (candidate == null || candidate.Tag.Equals(mirror.Tag) || candidate.Tag.Equals(seed.Tag)) continue;
                Body[] candidateBodies = candidate.GetBodies();
                if (candidateBodies == null) throw new InvalidOperationException("Output candidate body list is unavailable.");
                foreach (Body candidateBody in candidateBodies)
                {
                    if (candidateBody == null) throw new InvalidOperationException("Null output candidate body.");
                    if (!candidateBody.Tag.Equals(body.Tag)) continue;
                    if (output != null) throw new InvalidOperationException("Mirror body is shared by multiple candidate output features.");
                    if (candidateBodies.Length != 1 || !InPart(part, candidate)) throw new InvalidOperationException("Invalid output candidate.");
                    output = candidate;
                }
            }
            if (output == null) throw new InvalidOperationException("Mirror body has no unique output feature.");
            // The supported NX probe observed a null ContainerFeature. The
            // unique shared body identifies an output; it does not imply containment.
            return new Dictionary<string, object> {
                { "feature_id", Id(mirror, "mirror") }, { "seed_feature_id", Id(seed, "seed") },
                { "plane_mode", mode }, { "plane_origin_mm", origin }, { "plane_normal", normal },
                { "datum_plane_id", datumFeature == null ? null : Id(datumFeature, "datum") },
                { "body_id", Id(body, "body") }, { "body_tag", body.Tag.ToString() },
                { "output_feature_id", Id(output, "output") }, { "output_feature_tag", output.Tag.ToString() }
            };
        }
        finally
        {
            if (b != null) try { b.Destroy(); }
            catch (Exception ex) { throw new ReadStateUnknownException("Mirror builder destruction failed.", ex); }
        }
    }

    // The supplied deterministic inventory must cover every relevant part object and value.
    public static Dictionary<string, object> ReadIsolated(Session session, Part part, MirrorFeature mirror, Func<string> inventory)
    {
        if (session == null || part == null || mirror == null || inventory == null)
            throw new ArgumentException("Mirror reader requires session, part, feature and inventory.");
        bool dirty = part.IsModified;
        string before;
        try
        {
            before = inventory();
            if (before == null || part.IsModified != dirty) throw new InvalidOperationException("Initial inventory is missing or changed dirty state.");
        }
        catch (Exception ex) { throw new ReadStateUnknownException("Mirror reader could not establish an initial inventory.", ex); }
        Session.UndoMarkId mark;
        try { mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F19 isolated mirror reader"); }
        catch (Exception ex) { throw new ReadStateUnknownException("Mirror reader could not establish an undo mark.", ex); }
        Dictionary<string, object> data = null;
        Exception readError = null;
        try { data = ReadMaterialized(part, mirror); }
        catch (Exception ex) { readError = ex; }
        try
        {
            session.UndoToMarkWithStatus(mark, "F19 isolated mirror reader");
            int update = session.UpdateManager.DoUpdate(mark);
            if (update != 0) throw new InvalidOperationException("NX update returned " + update.ToString(CultureInfo.InvariantCulture));
            session.DeleteUndoMark(mark, "F19 isolated mirror reader");
            if (part.IsModified != dirty) throw new InvalidOperationException("Mirror read changed dirty state.");
            string after = inventory();
            if (part.IsModified != dirty || !String.Equals(before, after, StringComparison.Ordinal))
                throw new InvalidOperationException("Mirror read did not restore the captured part state.");
        }
        catch (Exception ex) { throw new ReadStateUnknownException("Mirror reader could not prove state restoration.", ex); }
        if (readError is ReadStateUnknownException) throw readError;
        if (readError != null)
        {
            InvalidOperationException invalid = readError as InvalidOperationException;
            throw new InvalidOperationException("Native mirror is not supported by the reader (" +
                readError.GetType().Name + (invalid == null ? "" : ": " + invalid.Message) + ").", readError);
        }
        return data;
    }
}
