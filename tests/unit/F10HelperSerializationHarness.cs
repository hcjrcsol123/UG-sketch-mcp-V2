using System;
using System.Collections.Generic;
using NxSketchMcp.M2;

internal static class F10HelperSerializationHarness
{
    private static int Main()
    {
        Dictionary<string, object> relation = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "feature_type", "CPROJ" }, { "feature_tag", "F-100" },
            { "source_tag", "S-200" }, { "source_journal_id_at_request", "ENTITY 1 1 1" },
            { "source_journal_id_after_edit", "ENTITY 1 2 1" },
            { "source_id_reindexed", true }, { "association_kind", "native_project_curve_input" }
        };
        Dictionary<string, object> request = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "operation_id", "f10-op-1" }, { "sketch_id", "SKETCH(2)" },
            { "source_curve_ids", new List<string> { "ENTITY 1 1 1", "ENTITY 2 1 1" } },
            { "direction_xyz", new double[] { 0.2672612419124244, 0.5345224838248488, 0.8017837257372732 } },
            { "associative", true }
        };
        Dictionary<string, object> plan = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "kind", "edit_sketch_projection" }, { "plan_id", "f10-plan-1" },
            { "revision_id", "rev-before" }, { "source_curve_ids", request["source_curve_ids"] },
            { "direction_xyz", request["direction_xyz"] }, { "associative", true },
            { "expected_output_count", 2 }
        };
        Dictionary<string, object> execution = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", "APPLIED" }, { "mutation_state", "APPLIED" },
            { "output_curve_ids", new List<string> { "ENTITY 3 1 1", "ENTITY 4 1 1" } },
            { "output_curve_tags", new List<string> { "O-300", "O-301" } },
            { "source_relation", relation }
        };
        Dictionary<string, object> verification = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "verified", true }, { "all_checks_verified", true },
            { "checks", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "projection_geometry", new Dictionary<string, object> { { "status", "VERIFIED" } } },
                    { "source_curves", new Dictionary<string, object> { { "status", "VERIFIED" } } },
                    { "reference_status", new Dictionary<string, object> { { "status", "VERIFIED" }, { "evidence", relation } } },
                    { "history", new Dictionary<string, object> { { "status", "VERIFIED" } } }
                } }
        };
        Dictionary<string, object> response = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "request", request }, { "plan", plan }, { "execution", execution }, { "verification", verification }
        };
        Console.WriteLine(M2Json.CanonicalText(response));
        return 0;
    }
}
