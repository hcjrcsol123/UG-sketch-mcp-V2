using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using NXOpen;
using NXOpen.Annotations;
using NxSketchMcp.M2;

/// <summary>Native result from one sketch reference-state conversion.</summary>
public sealed class V2SketchReferenceConversionNativeResult
{
    public NXObject CommitObject;
    public NXObject[] CommittedObjects;
    public Dictionary<string, object> Evidence;
}

/// <summary>Runs NX's native Convert to/from Reference sketch operation.</summary>
public static class V2SketchReferenceConversionFactory
{
    public static V2SketchReferenceConversionNativeResult Execute(Part part, Sketch targetSketch,
        NXObject[] inputObjects, string targetState)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (targetSketch == null) throw new ArgumentNullException("targetSketch");
        if (inputObjects == null) throw new ArgumentNullException("inputObjects");
        if (inputObjects.Length == 0)
            throw new ArgumentException("At least one native Sketch curve or dimension is required.", "inputObjects");
        if (targetState != "reference" && targetState != "active")
            throw new ArgumentException("targetState must be reference or active.", "targetState");

        BasePart targetPart = targetSketch.OwningPart;
        if (targetPart == null || targetPart.Tag != part.Tag)
            throw new ArgumentException("targetSketch must belong to the supplied Work Part.", "targetSketch");

        NXObject[] inputs = (NXObject[])inputObjects.Clone();
        HashSet<string> inputTags = new HashSet<string>(StringComparer.Ordinal);
        List<object> inputTagRecords = new List<object>();
        List<object> inputTypeRecords = new List<object>();
        for (int i = 0; i < inputs.Length; i++)
        {
            NXObject input = inputs[i];
            if (input == null)
                throw new ArgumentException("inputObjects cannot contain null entries.", "inputObjects");
            if (!(input is Curve) && !(input is Dimension))
                throw new ArgumentException("Every input must be a native Sketch Curve or Dimension.", "inputObjects");
            BasePart inputPart = input.OwningPart;
            if (inputPart == null || inputPart.Tag != part.Tag)
                throw new ArgumentException("Every input object must belong to the supplied Work Part.", "inputObjects");
            string tag = input.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || !inputTags.Add(tag))
                throw new ArgumentException("inputObjects must have non-empty, unique Tags.", "inputObjects");
            Curve curve = input as Curve;
            if (curve != null && !targetSketch.IsSketchGeometry(curve))
                throw new ArgumentException("Every Curve input must belong to targetSketch.", "inputObjects");
            Dimension dimension = input as Dimension;
            if (dimension != null && !targetSketch.IsSketchDimension(dimension))
                throw new ArgumentException("Every Dimension input must belong to targetSketch.", "inputObjects");
            inputTagRecords.Add(tag);
            inputTypeRecords.Add(input.GetType().Name);
        }

        ConvertToFromReferenceBuilder builder = null;
        V2SketchReferenceConversionNativeResult result = null;
        Exception operationException = null;
        try
        {
            builder = part.Sketches.CreateConvertToFromReferenceBuilder();
            if (builder == null)
                throw new M2ModelException("REFERENCE_CONVERSION_BUILDER_INVALID",
                    "NX returned no ConvertToFromReferenceBuilder");

            builder.OutputState = targetState == "reference"
                ? ConvertToFromReferenceBuilder.OutputType.Reference
                : ConvertToFromReferenceBuilder.OutputType.Active;
            builder.SelectAllProjectFeatureCurves = false;
            for (int i = 0; i < inputs.Length; i++) builder.InputObjects.Add(inputs[i]);

            ConvertToFromReferenceBuilder.OutputType outputStateReadback = builder.OutputState;
            bool selectAllProjectFeatureCurves = builder.SelectAllProjectFeatureCurves;
            NXObject[] selectedObjects = builder.InputObjects.GetArray();
            if (selectedObjects == null) selectedObjects = new NXObject[0];
            HashSet<string> selectedTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (NXObject selected in selectedObjects)
            {
                if (selected == null || !selectedTags.Add(selected.Tag.ToString()))
                    throw new M2ModelException("REFERENCE_CONVERSION_INPUT_INVALID",
                        "NX builder input list contains a null or duplicate object");
            }
            if (selectedTags.Count != inputTags.Count || !selectedTags.SetEquals(inputTags))
                throw new M2ModelException("REFERENCE_CONVERSION_INPUT_INVALID",
                    "NX builder input list differs from the explicitly selected objects");
            if (outputStateReadback != (targetState == "reference"
                ? ConvertToFromReferenceBuilder.OutputType.Reference
                : ConvertToFromReferenceBuilder.OutputType.Active) || selectAllProjectFeatureCurves)
                throw new M2ModelException("REFERENCE_CONVERSION_OPTIONS_INVALID",
                    "NX did not retain the requested output state or explicit single-object selection policy");

            bool valid = builder.Validate();
            if (!valid)
                throw new M2ModelException("REFERENCE_CONVERSION_INVALID",
                    "NX ConvertToFromReferenceBuilder rejected its explicitly selected objects");

            NXObject commitObject = builder.Commit();
            NXObject[] committedObjects = builder.GetCommittedObjects();
            if (committedObjects == null) committedObjects = new NXObject[0];
            List<object> committedTypes = new List<object>();
            foreach (NXObject committed in committedObjects)
                committedTypes.Add(committed == null ? null : committed.GetType().Name);

            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
            evidence["native_engine"] = "NXOpen.ConvertToFromReferenceBuilder";
            evidence["target_sketch_tag"] = targetSketch.Tag.ToString();
            evidence["target_state"] = targetState;
            evidence["output_state_readback"] = outputStateReadback.ToString();
            evidence["select_all_project_feature_curves"] = selectAllProjectFeatureCurves;
            evidence["input_count"] = inputs.Length;
            evidence["input_tags"] = inputTagRecords;
            evidence["input_object_types"] = inputTypeRecords;
            evidence["builder_input_tags"] = M3ReferenceConversionTags(selectedObjects);
            evidence["builder_validate"] = valid;
            evidence["commit_method"] = "Commit";
            evidence["commit_returned_null"] = commitObject == null;
            evidence["commit_object_type"] = commitObject == null ? null : commitObject.GetType().Name;
            evidence["committed_object_count"] = committedObjects.Length;
            evidence["committed_object_types"] = committedTypes;
            result = new V2SketchReferenceConversionNativeResult
            {
                CommitObject = commitObject,
                CommittedObjects = committedObjects,
                Evidence = evidence
            };
        }
        catch (Exception ex) { operationException = ex; }

        Exception destroyException = null;
        if (builder != null)
        {
            try { builder.Destroy(); }
            catch (Exception ex) { destroyException = ex; }
        }

        if (operationException != null) ExceptionDispatchInfo.Capture(operationException).Throw();
        if (destroyException != null)
            throw new InvalidOperationException(
                "NX reference conversion completed but its builder could not be destroyed.", destroyException);
        return result;
    }

    private static List<object> M3ReferenceConversionTags(NXObject[] objects)
    {
        List<object> result = new List<object>();
        if (objects == null) return result;
        foreach (NXObject item in objects) result.Add(item == null ? null : item.Tag.ToString());
        return result;
    }
}
