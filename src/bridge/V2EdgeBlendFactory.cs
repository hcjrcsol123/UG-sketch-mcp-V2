using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;

/// <summary>Builder cleanup failed; the caller must treat the NX model state as unknown.</summary>
public sealed class V2EdgeBlendBuilderCleanupException : Exception
{
    public V2EdgeBlendBuilderCleanupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Constant circular edge blends. The caller owns the NX transaction and rollback.</summary>
public static class V2EdgeBlendFactory
{
    public static Feature Create(Part part, Edge[] edges, double radius)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (part.PartUnits != BasePart.Units.Millimeters) throw new ArgumentException("Only millimeter parts are supported.", "part");
        if (edges == null || edges.Length == 0) throw new ArgumentException("At least one edge is required.", "edges");
        if (Double.IsNaN(radius) || Double.IsInfinity(radius) || radius <= 0.0)
            throw new ArgumentOutOfRangeException("radius", "Radius must be finite and positive, in part length units.");
        HashSet<string> expectedTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Edge edge in edges)
        {
            if (edge == null || edge.OwningPart == null || !edge.OwningPart.Tag.Equals(part.Tag))
                throw new ArgumentException("Every edge must belong to the supplied part.", "edges");
            if (!expectedTags.Add(edge.Tag.ToString())) throw new ArgumentException("Edges must be distinct.", "edges");
        }
        EdgeBlendBuilder builder = null;
        Feature feature;
        try
        {
            builder = part.Features.CreateEdgeBlendBuilder(null);
            if (builder == null) throw new InvalidOperationException("NX did not create an edge blend builder.");
            builder.AllInstancesOption = false;
            builder.RollOntoEdge = false;
            builder.RollOverSmoothEdge = false;
            builder.LimitFailingAreas = false;
            builder.PatchComplexGeometryAreas = false;
            builder.MoveSharpEdge = false;
            ScCollector collector = part.ScCollectors.CreateCollector();
            if (collector == null) throw new InvalidOperationException("NX did not create an edge blend collector.");
            EdgeDumbRule rule = part.ScRuleFactory.CreateRuleEdgeDumb(edges);
            if (rule == null) throw new InvalidOperationException("NX did not create an edge dumb rule.");
            collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
            int index = builder.AddChainset(collector, radius.ToString("R", CultureInfo.InvariantCulture));
            if (index != 0 || builder.GetNumberOfValidChainsets() != 1)
                throw new InvalidOperationException("NX did not create exactly one edge blend chainset at index zero.");
            if (!builder.Validate()) throw new InvalidOperationException("NX rejected the edge blend definition.");
            feature = builder.CommitFeature();
            if (!(feature is EdgeBlend)) throw new InvalidOperationException("NX did not return a native EdgeBlend feature.");
        }
        finally { if (builder != null) Destroy(builder); }
        Dictionary<string, object> read = Read(part, feature);
        string[] actualTags = (string[])read["edge_tags"];
        HashSet<string> actual = new HashSet<string>(actualTags, StringComparer.Ordinal);
        double observed = (double)read["radius"];
        if (actual.Count != actualTags.Length || !actual.SetEquals(expectedTags) ||
            Math.Abs(observed - radius) > Math.Max(1e-12, Math.Abs(radius) * 1e-10))
            throw new InvalidOperationException("Native edge blend selection or radius does not match the request.");
        return feature;
    }

    public static Dictionary<string, object> Read(Part part, Feature feature)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (part.PartUnits != BasePart.Units.Millimeters) throw new ArgumentException("Only millimeter parts are supported.", "part");
        EdgeBlend blend = feature as EdgeBlend;
        if (blend == null || blend.OwningPart == null || !blend.OwningPart.Tag.Equals(part.Tag))
            throw new ArgumentException("A native EdgeBlend feature in the supplied part is required.", "feature");
        EdgeBlendBuilder builder = null;
        try
        {
            builder = part.Features.CreateEdgeBlendBuilder(blend);
            if (builder == null) throw new InvalidOperationException("NX did not reopen the edge blend feature.");
            int chainsetCount = builder.GetNumberOfValidChainsets();
            if (chainsetCount != 1) throw new InvalidOperationException("Expected exactly one valid edge blend chainset.");
            ScCollector collector;
            EdgeBlendBuilder.Section section;
            EdgeBlendBuilder.Conic conic;
            EdgeBlendBuilder.Rhotype rho;
            Expression radius, center, rhoValue;
            builder.GetChainsetAndSectionValue(0, out collector, out section, out conic, out rho, out radius, out center, out rhoValue);
            if (collector == null || radius == null || radius.Units == null || section != EdgeBlendBuilder.Section.Circular)
                throw new InvalidOperationException("Expected a circular edge blend with a unit-bearing radius.");
            if (Double.IsNaN(radius.Value) || Double.IsInfinity(radius.Value) || radius.Value <= 0.0)
                throw new InvalidOperationException("Edge blend radius is not finite and positive.");
            SelectionIntentRule[] rules;
            collector.GetRules(out rules);
            if (rules == null || rules.Length != 1 || rules[0] == null || rules[0].Type != SelectionIntentRule.RuleType.EdgeDumb)
                throw new InvalidOperationException("Expected exactly one edge dumb selection rule.");
            EdgeDumbRule dumbRule = rules[0] as EdgeDumbRule;
            if (dumbRule == null)
                throw new InvalidOperationException("NX did not expose the edge dumb rule as an EdgeDumbRule; rule data cannot be verified.");
            Edge[] ruleEdges;
            dumbRule.GetData(out ruleEdges);
            if (ruleEdges == null || ruleEdges.Length == 0)
                throw new InvalidOperationException("NX returned no edges in the edge dumb rule.");
            string[] ruleEdgeTags = new string[ruleEdges.Length];
            HashSet<string> ruleTags = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ruleEdges.Length; i++)
            {
                if (ruleEdges[i] == null) throw new InvalidOperationException("Edge dumb rule contains an invalid edge.");
                ruleEdgeTags[i] = ruleEdges[i].Tag.ToString();
                if (!ruleTags.Add(ruleEdgeTags[i])) throw new InvalidOperationException("Edge dumb rule contains duplicate edges.");
            }
            TaggedObject[] objects = collector.GetObjects();
            if (objects == null || objects.Length == 0) throw new InvalidOperationException("Edge blend collector is empty.");
            string[] edgeTags = new string[objects.Length];
            string[] edgeIds = new string[objects.Length];
            int[][] variableCounts = new int[objects.Length][];
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < objects.Length; i++)
            {
                Edge edge = objects[i] as Edge;
                if (edge == null) throw new InvalidOperationException("Edge blend collector contains a non-edge object.");
                // Input edges may be suppressed after commit; never query their geometry or journal identifier.
                edgeTags[i] = edge.Tag.ToString();
                edgeIds[i] = edge.JournalIdentifier;
                if (String.IsNullOrWhiteSpace(edgeIds[i])) throw new InvalidOperationException("Edge blend input identifier is unavailable.");
                if (!seen.Add(edgeTags[i])) throw new InvalidOperationException("Edge blend collector contains duplicate edges.");
                Expression[] parameters, radii;
                Point[] points;
                bool[] external;
                builder.GetVariableRadiusDataNew(edge, out parameters, out radii, out points, out external);
                if (parameters == null || radii == null || points == null || external == null)
                    throw new InvalidOperationException("NX returned incomplete variable-radius data.");
                variableCounts[i] = new int[] { parameters.Length, radii.Length, points.Length, external.Length };
                if (parameters.Length != 0 || radii.Length != 0 || points.Length != 0 || external.Length != 0)
                    throw new InvalidOperationException("Variable-radius edge blends are unsupported.");
            }
            if (!seen.SetEquals(ruleTags))
                throw new InvalidOperationException("Edge dumb rule edges do not match the collector edges.");
            if (builder.AllInstancesOption || builder.RollOntoEdge || builder.RollOverSmoothEdge ||
                builder.LimitFailingAreas || builder.PatchComplexGeometryAreas || builder.MoveSharpEdge ||
                builder.GetNumberOfValidSetbackData() != 0)
                throw new InvalidOperationException("Unsupported edge blend expansion or recovery options.");
            string featureId = blend.JournalIdentifier;
            string units = radius.Units.JournalIdentifier;
            if (String.IsNullOrWhiteSpace(featureId) || units != "MilliMeter")
                throw new InvalidOperationException("NX returned an unidentified feature or a radius not expressed in millimeters.");
            List<Dictionary<string, object>> expressions = FeatureExpressions(blend);
            return new Dictionary<string, object> {
                { "feature_id", featureId }, { "feature_tag", blend.Tag.ToString() },
                { "chainset_count", chainsetCount }, { "radius", radius.Value }, { "radius_mm", radius.Value },
                { "radius_expression_id", radius.JournalIdentifier }, { "radius_expression_tag", radius.Tag.ToString() },
                { "feature_expressions", expressions },
                { "radius_formula", radius.RightHandSide }, { "radius_units", units },
                { "section", section.ToString() }, { "edge_tags", edgeTags }, { "edge_ids", edgeIds },
                { "rule_types", new string[] { rules[0].Type.ToString() } },
                { "rule_edge_tags", ruleEdgeTags }, { "rule_edges_match_collector", true },
                { "variable_radius_counts", variableCounts },
                { "all_instances", builder.AllInstancesOption }, { "roll_onto_edge", builder.RollOntoEdge },
                { "roll_over_smooth_edge", builder.RollOverSmoothEdge }, { "limit_failing_areas", builder.LimitFailingAreas },
                { "patch_complex_geometry_areas", builder.PatchComplexGeometryAreas }, { "move_sharp_edge", builder.MoveSharpEdge },
                { "remove_self_intersection", builder.RemoveSelfIntersection }, { "overlap_option", builder.OverlapOption.ToString() },
                { "blend_order", builder.BlendOrder.ToString() }, { "blend_face_continuity", builder.BlendFaceContinuity.ToString() },
                { "convex_concave_y", builder.ConvexConcaveY }, { "segment_blend_faces", builder.SegmentBlendFaces },
                { "setback_option", builder.SetbackOption.ToString() }, { "setback_count", builder.GetNumberOfValidSetbackData() },
                { "trimming_option", builder.TrimmingOption }, { "tolerance", builder.Tolerance },
                { "zero_slope_radius_function_at_chain_ends", builder.ZeroSlopeRadiusFunctionAtChainEnds }
            };
        }
        finally { if (builder != null) Destroy(builder); }
    }

    private static List<Dictionary<string, object>> FeatureExpressions(Feature feature)
    {
        Expression[] values = feature.GetExpressions();
        if (values == null) throw new InvalidOperationException("NX returned no expression list for the edge blend.");
        List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
        foreach (Expression expression in values)
        {
            if (expression == null) throw new InvalidOperationException("NX returned an invalid edge blend expression.");
            Feature owner = expression.GetOwningFeature();
            Feature[] users = expression.GetUsingFeatures();
            if (users == null) throw new InvalidOperationException("NX returned no expression user list.");
            string[] userIds = new string[users.Length];
            string[] userTags = new string[users.Length];
            for (int i = 0; i < users.Length; i++)
            {
                if (users[i] == null) throw new InvalidOperationException("NX returned an invalid expression user.");
                userIds[i] = users[i].JournalIdentifier;
                userTags[i] = users[i].Tag.ToString();
            }
            result.Add(new Dictionary<string, object> {
                { "id", expression.JournalIdentifier }, { "tag", expression.Tag.ToString() },
                { "name", expression.Name }, { "type", expression.Type }, { "rhs", expression.RightHandSide },
                { "listed_by_feature_id", feature.JournalIdentifier },
                { "owner_feature_id", owner == null ? null : owner.JournalIdentifier },
                { "owner_feature_tag", owner == null ? null : owner.Tag.ToString() },
                { "user_feature_ids", userIds }, { "user_feature_tags", userTags }
            });
        }
        return result;
    }

    private static void Destroy(EdgeBlendBuilder builder)
    {
        try { builder.Destroy(); }
        catch (Exception error) { throw new V2EdgeBlendBuilderCleanupException("NX edge blend builder destruction failed; model state is unknown.", error); }
    }
}
