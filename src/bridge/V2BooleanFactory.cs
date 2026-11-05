using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.Features;

public sealed class V2BooleanBuilderCleanupException : Exception
{
    public V2BooleanBuilderCleanupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Native Boolean feature operations. The caller owns the NX transaction and rollback.</summary>
public static class V2BooleanFactory
{
    public static Feature Create(Part part, string operation, Body target, Body[] tools, bool retainTools)
    {
        if (part == null) throw new ArgumentNullException("part");
        Feature.BooleanType nativeOperation = ParseOperation(operation);
        CheckBody(part, target, "target");
        if (tools == null || tools.Length == 0) throw new ArgumentException("At least one tool body is required.", "tools");
        HashSet<Tag> tags = new HashSet<Tag>();
        tags.Add(target.Tag);
        foreach (Body body in tools)
        {
            CheckBody(part, body, "tools");
            if (!tags.Add(body.Tag)) throw new ArgumentException("Target and tool bodies must be distinct.", "tools");
        }
        string[] expectedTargetTags = Tags(new Body[] { target });
        string[] expectedToolTags = Tags(tools);
        BooleanBuilder builder = null;
        Feature feature;
        try
        {
            builder = part.Features.CreateBooleanBuilderUsingCollector(null);
            if (builder == null) throw new InvalidOperationException("NX did not create a Boolean builder.");
            builder.Operation = nativeOperation;
            builder.RetainTarget = false;
            builder.RetainTool = retainTools;
            builder.ConvertToSew = false;
            ScCollector targetCollector = part.ScCollectors.CreateCollector();
            ScCollector toolCollector = part.ScCollectors.CreateCollector();
            if (targetCollector == null || toolCollector == null) throw new InvalidOperationException("NX did not create the Boolean body collectors.");
            targetCollector.ReplaceRules(new SelectionIntentRule[] { part.ScRuleFactory.CreateRuleBodyDumb(new Body[] { target }) }, false);
            toolCollector.ReplaceRules(new SelectionIntentRule[] { part.ScRuleFactory.CreateRuleBodyDumb(tools) }, false);
            builder.TargetBodyCollector = targetCollector;
            builder.ToolBodyCollector = toolCollector;
            if (!builder.Validate()) throw new InvalidOperationException("NX rejected the Boolean definition.");
            feature = builder.CommitFeature();
            if (!(feature is BooleanFeature)) throw new InvalidOperationException("NX did not return a native BooleanFeature.");
        }
        finally { if (builder != null) Destroy(builder); }
        Dictionary<string, object> read = Read(part, feature);
        if ((string)read["operation"] != operation || (bool)read["retain_tools"] != retainTools ||
            !SameTags((string[])read["target_body_tags"], expectedTargetTags) ||
            !SameTags((string[])read["tool_body_tags"], expectedToolTags))
            throw new InvalidOperationException("Native Boolean associations or retain policy do not match the request: operation=" + operation + "/" + read["operation"] +
                ", retain=" + retainTools + "/" + read["retain_tools"] + ", target_tags=" + String.Join(",", expectedTargetTags) + "/" + String.Join(",", (string[])read["target_body_tags"]) +
                ", tool_tags=" + String.Join(",", expectedToolTags) + "/" + String.Join(",", (string[])read["tool_body_tags"]) + ".");
        return feature;
    }

    public static Dictionary<string, object> Read(Part part, Feature feature)
    {
        if (part == null) throw new ArgumentNullException("part");
        BooleanFeature boolean = feature as BooleanFeature;
        if (boolean == null || boolean.OwningPart == null || !boolean.OwningPart.Tag.Equals(part.Tag))
            throw new ArgumentException("A native BooleanFeature in the supplied part is required.", "feature");
        BooleanBuilder builder = null;
        try
        {
            builder = part.Features.CreateBooleanBuilderUsingCollector(boolean);
            if (builder == null) throw new InvalidOperationException("NX did not reopen the Boolean feature.");
            string operation = OperationName(builder.Operation);
            if (builder.RetainTarget || builder.CopyTargets || builder.CopyTools != builder.RetainTool || builder.ConvertToSew)
                throw new InvalidOperationException("Unsupported or inconsistent native Boolean target, retain, or sew policy.");
            Body[] targets = CollectorBodies(part, builder.TargetBodyCollector, "target");
            Body[] tools = CollectorBodies(part, builder.ToolBodyCollector, "tool");
            if (targets.Length != 1 || tools.Length == 0) throw new InvalidOperationException("Expected one Boolean target and at least one tool.");
            HashSet<Tag> inputs = new HashSet<Tag>();
            foreach (Body body in targets) inputs.Add(body.Tag);
            foreach (Body body in tools) if (!inputs.Add(body.Tag)) throw new InvalidOperationException("Boolean input bodies are not distinct.");
            Body[] outputs = feature.GetBodies();
            if (outputs == null || outputs.Length == 0) throw new InvalidOperationException("NX returned no Boolean output bodies.");
            HashSet<Tag> outputTags = new HashSet<Tag>();
            foreach (Body body in outputs)
            {
                CheckBody(part, body, "output");
                if (!outputTags.Add(body.Tag)) throw new InvalidOperationException("NX returned duplicate Boolean output bodies.");
            }
            return new Dictionary<string, object> {
                { "feature_id", Id(feature) }, { "feature_tag", feature.Tag.ToString() },
                { "operation", operation }, { "native_operation", builder.Operation.ToString() },
                { "retain_tools", builder.RetainTool }, { "retain_target", builder.RetainTarget },
                { "copy_targets", builder.CopyTargets }, { "copy_tools", builder.CopyTools },
                { "target_body_ids", Ids(targets) }, { "target_body_tags", Tags(targets) },
                { "tool_body_ids", Ids(tools) }, { "tool_body_tags", Tags(tools) },
                { "output_body_ids", Ids(outputs) }, { "output_body_tags", Tags(outputs) }
            };
        }
        finally { if (builder != null) Destroy(builder); }
    }

    private static Feature.BooleanType ParseOperation(string operation)
    {
        if (operation == "unite") return Feature.BooleanType.Unite;
        if (operation == "subtract") return Feature.BooleanType.Subtract;
        if (operation == "intersect") return Feature.BooleanType.Intersect;
        throw new ArgumentException("Operation must be unite, subtract, or intersect.", "operation");
    }
    private static string OperationName(Feature.BooleanType operation)
    {
        if (operation == Feature.BooleanType.Unite) return "unite";
        if (operation == Feature.BooleanType.Subtract) return "subtract";
        if (operation == Feature.BooleanType.Intersect) return "intersect";
        throw new InvalidOperationException("Unsupported native Boolean operation.");
    }
    private static void CheckBody(Part part, Body body, string label)
    {
        if (body == null || body.OwningPart == null || !body.OwningPart.Tag.Equals(part.Tag) || !body.IsSolidBody)
            throw new ArgumentException("A solid " + label + " body in the supplied part is required.", label);
    }
    private static Body[] CollectorBodies(Part part, ScCollector collector, string label)
    {
        if (collector == null) throw new InvalidOperationException("Missing native Boolean " + label + " collector.");
        TaggedObject[] objects = collector.GetObjects();
        if (objects == null) throw new InvalidOperationException("Cannot read native Boolean " + label + " collector.");
        Body[] bodies = new Body[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            bodies[i] = objects[i] as Body;
            CheckBody(part, bodies[i], label);
        }
        return bodies;
    }
    private static string Id(NXObject obj)
    {
        string id = obj.JournalIdentifier;
        if (String.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("NX object has no journal identifier.");
        return id;
    }
    private static string[] Ids(Body[] bodies)
    {
        string[] ids = new string[bodies.Length];
        for (int i = 0; i < bodies.Length; i++) ids[i] = Id(bodies[i]);
        return ids;
    }
    private static string[] Tags(Body[] bodies)
    {
        string[] tags = new string[bodies.Length];
        for (int i = 0; i < bodies.Length; i++) tags[i] = bodies[i].Tag.ToString();
        return tags;
    }
    private static bool SameTags(string[] observed, string[] expected)
    {
        if (observed.Length != expected.Length) return false;
        HashSet<string> tags = new HashSet<string>(observed, StringComparer.Ordinal);
        if (tags.Count != expected.Length) return false;
        foreach (string tag in expected) if (!tags.Remove(tag)) return false;
        return tags.Count == 0;
    }
    private static void Destroy(BooleanBuilder builder)
    {
        try { builder.Destroy(); }
        catch (Exception error) { throw new V2BooleanBuilderCleanupException("NX Boolean builder destruction failed; model state is unknown.", error); }
    }
}
