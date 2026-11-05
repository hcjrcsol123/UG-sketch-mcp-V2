using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public sealed class V2ChamferBuilderCleanupException : Exception
{
    public V2ChamferBuilderCleanupException(string message, Exception inner) : base(message, inner) { }
}

public static class V2ChamferFactory
{
    public static Feature Create(Part part, Edge[] edges, M3ChamferRequest request, bool reverse)
    {
        if (part == null || request == null || edges == null || edges.Length != 1 || edges[0] == null ||
            edges[0].OwningPart == null || !edges[0].OwningPart.Tag.Equals(part.Tag))
            throw new ArgumentException("One edge owned by the part is required.");
        ChamferBuilder builder = null;
        try {
            builder = part.Features.CreateChamferBuilder(null);
            if (builder == null) throw new InvalidOperationException("NX did not create a chamfer builder.");
            ScCollector collector = part.ScCollectors.CreateCollector();
            EdgeDumbRule rule = part.ScRuleFactory.CreateRuleEdgeDumb(edges);
            if (collector == null || rule == null) throw new InvalidOperationException("NX did not create a selection rule.");
            collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
            builder.SmartCollector = collector;
            builder.Method = ChamferBuilder.OffsetMethod.EdgesAlongFaces;
            builder.Option = request.Mode == "equal_distance" ? ChamferBuilder.ChamferOption.SymmetricOffsets :
                request.Mode == "two_distances" ? ChamferBuilder.ChamferOption.TwoOffsets : ChamferBuilder.ChamferOption.OffsetAndAngle;
            builder.ReverseOffsets = reverse;
            builder.AllInstances = false;
            builder.FirstOffsetExp.RightHandSide = request.DistanceMm.ToString("R", CultureInfo.InvariantCulture);
            if (request.Mode == "two_distances") builder.SecondOffsetExp.RightHandSide = request.SecondDistanceMm.ToString("R", CultureInfo.InvariantCulture);
            if (request.Mode == "distance_angle") builder.AngleExp.RightHandSide = request.AngleDeg.ToString("R", CultureInfo.InvariantCulture);
            if (!builder.Validate()) throw new InvalidOperationException("NX rejected the chamfer definition.");
            Feature feature = builder.CommitFeature();
            if (!(feature is Chamfer)) throw new InvalidOperationException("NX did not return a native Chamfer.");
            return feature;
        } finally { if (builder != null) Destroy(builder); }
    }

    public static void FlipOffsets(Part part, Feature feature)
    {
        Chamfer chamfer=feature as Chamfer;
        if (part==null || chamfer==null || chamfer.OwningPart==null || !chamfer.OwningPart.Tag.Equals(part.Tag))
            throw new ArgumentException("The native chamfer must belong to the part.");
        ChamferBuilder b=null;
        try {
            b=part.Features.CreateChamferBuilder(chamfer);
            if(b==null) throw new InvalidOperationException("NX did not reopen the chamfer for direction correction.");
            b.ReverseOffsets=!b.ReverseOffsets;
            if(!b.Validate()) throw new InvalidOperationException("NX rejected the chamfer direction correction.");
            Feature changed=b.CommitFeature();
            if(changed==null || !changed.Tag.Equals(chamfer.Tag)) throw new InvalidOperationException("NX changed chamfer feature identity during direction correction.");
        } finally {if(b!=null) Destroy(b);}
    }

    private static Dictionary<string,object> Expr(Expression e)
    {
        if (e == null || e.Units == null || Double.IsNaN(e.Value) || Double.IsInfinity(e.Value))
            throw new InvalidOperationException("Native chamfer expression is missing or invalid.");
        return new Dictionary<string,object> {{"value",e.Value},{"units",e.Units.JournalIdentifier},{"rhs",e.RightHandSide},
            {"id",e.JournalIdentifier},{"tag",e.Tag.ToString()}};
    }

    public static Dictionary<string,object> Read(Part part, Feature feature)
    {
        Chamfer chamfer = feature as Chamfer;
        if (part == null || part.PartUnits != BasePart.Units.Millimeters || chamfer == null ||
            chamfer.OwningPart == null || !chamfer.OwningPart.Tag.Equals(part.Tag))
            throw new ArgumentException("A native Chamfer in the millimeter part is required.");
        ChamferBuilder b = null;
        try {
            b = part.Features.CreateChamferBuilder(chamfer);
            if (b == null || b.SmartCollector == null || b.AllInstances || b.Method != ChamferBuilder.OffsetMethod.EdgesAlongFaces)
                throw new InvalidOperationException("Unsupported native chamfer selection or options.");
            if (b.Option != ChamferBuilder.ChamferOption.SymmetricOffsets && b.Option != ChamferBuilder.ChamferOption.TwoOffsets && b.Option != ChamferBuilder.ChamferOption.OffsetAndAngle)
                throw new InvalidOperationException("Unsupported native chamfer mode.");
            SelectionIntentRule[] rules; b.SmartCollector.GetRules(out rules);
            if (rules == null || rules.Length != 1 || !(rules[0] is EdgeDumbRule)) throw new InvalidOperationException("Expected one edge dumb rule.");
            Edge[] ruleEdges; ((EdgeDumbRule)rules[0]).GetData(out ruleEdges);
            TaggedObject[] objects = b.SmartCollector.GetObjects();
            if (objects == null || objects.Length != 1 || ruleEdges == null || ruleEdges.Length != 1 || !(objects[0] is Edge) || ruleEdges[0] == null || !objects[0].Tag.Equals(ruleEdges[0].Tag))
                throw new InvalidOperationException("Chamfer collector and rule edge differ.");
            Edge edge=(Edge)objects[0];
            if (String.IsNullOrWhiteSpace(edge.JournalIdentifier)) throw new InvalidOperationException("Chamfer edge ID unavailable.");
            return new Dictionary<string,object> {
                {"feature_id",chamfer.JournalIdentifier},{"feature_tag",chamfer.Tag.ToString()},
                {"option",b.Option.ToString()},{"method",b.Method.ToString()},{"reverse_offsets",b.ReverseOffsets},
                {"all_instances",b.AllInstances},{"save_feature",b.SaveFeature},{"tolerance",b.Tolerance},{"first_offset",Expr(b.FirstOffsetExp)},
                {"second_offset",Expr(b.SecondOffsetExp)},{"angle",Expr(b.AngleExp)},
                {"edge_ids",new string[]{edge.JournalIdentifier}},{"edge_tags",new string[]{edge.Tag.ToString()}},
                {"rule_types",new string[]{rules[0].Type.ToString()}},{"rule_edge_tags",new string[]{ruleEdges[0].Tag.ToString()}},
                {"rule_edges_match_collector",true},{"feature_expressions",FeatureExpressions(chamfer)}
            };
        } finally {if (b != null) Destroy(b);}
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

    private static void Destroy(ChamferBuilder builder)
    {
        try {builder.Destroy();} catch(Exception ex) {throw new V2ChamferBuilderCleanupException("NX chamfer builder destruction failed; model state is unknown.",ex);}
    }
}
