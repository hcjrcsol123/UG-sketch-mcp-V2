using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NxSketchMcp.M2;

/// <summary>F12's allowed-change audit across one append-only Extrude commit.</summary>
public sealed partial class M2OpenModelAdapter
{
    private static bool F12SheetSurfaceAreaMatches(Part work, List<M3BodyMeasure> bodies,
        Dictionary<string, object> profileAnalysis, double spanMm,
        out double? expectedAreaMm2, out double? actualAreaMm2,
        out Dictionary<string, object> evidence)
    {
        expectedAreaMm2 = null;
        actualAreaMm2 = null;
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> inputRecords = profileAnalysis != null && profileAnalysis.ContainsKey("input_geometry_records")
            ? profileAnalysis["input_geometry_records"] as List<object> : null;
        if (work == null || bodies == null || bodies.Count == 0 || inputRecords == null ||
            !IsFinite(spanMm) || spanMm <= 0.0)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "sheet bodies, active profile records, or finite span are unavailable");
            return false;
        }
        double totalCurveLength = 0.0;
        foreach (object value in inputRecords)
        {
            Dictionary<string, object> source = value as Dictionary<string, object>;
            Dictionary<string, object> geometry = source != null && source.ContainsKey("geometry")
                ? source["geometry"] as Dictionary<string, object> : null;
            object rawLength;
            double length;
            if (geometry == null || !geometry.TryGetValue("length_mm", out rawLength) ||
                !TryFiniteNumber(rawLength, out length) || length <= 0.0)
            {
                evidence.Add("status", "MISMATCH");
                evidence.Add("reason", "active Section input has no positive finite curve length");
                return false;
            }
            totalCurveLength += length;
            if (!IsFinite(totalCurveLength))
            {
                evidence.Add("status", "MISMATCH");
                evidence.Add("reason", "profile curve length sum overflowed");
                return false;
            }
        }
        double expected = totalCurveLength * spanMm;
        if (!IsFinite(expected) || expected <= 0.0)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "expected sheet area is not positive and finite");
            return false;
        }
        expectedAreaMm2 = expected;
        double totalArea = 0.0;
        List<object> bodyAreas = new List<object>();
        foreach (M3BodyMeasure bodyMeasure in bodies)
        {
            if (bodyMeasure == null || bodyMeasure.Body == null || bodyMeasure.IsSolid)
            {
                evidence.Add("status", "MISMATCH");
                evidence.Add("reason", "explicit sheet request returned a solid or missing body");
                return false;
            }
            Dictionary<string, object> geometry = CaptureAuditedF12BodyGeometry(work, bodyMeasure.Body);
            List<object> faces = geometry["faces"] as List<object>;
            if (faces == null || faces.Count == 0)
            {
                evidence.Add("status", "MISMATCH");
                evidence.Add("reason", "sheet result has no measurable faces");
                return false;
            }
            double bodyArea = 0.0;
            foreach (object faceValue in faces)
            {
                Dictionary<string, object> face = faceValue as Dictionary<string, object>;
                object rawArea;
                double area;
                if (face == null || !face.TryGetValue("area_mm2", out rawArea) ||
                    !TryFiniteNumber(rawArea, out area) || area <= 0.0)
                {
                    evidence.Add("status", "MISMATCH");
                    evidence.Add("reason", "sheet face lacks a positive finite native surface area");
                    return false;
                }
                bodyArea += area;
            }
            if (!IsFinite(bodyArea) || bodyArea <= 0.0)
            {
                evidence.Add("status", "MISMATCH");
                evidence.Add("reason", "sheet body has zero or invalid surface area");
                return false;
            }
            totalArea += bodyArea;
            bodyAreas.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "body_tag", bodyMeasure.Body.Tag.ToString() },
                { "face_count", faces.Count },
                { "surface_area_mm2", bodyArea },
                { "solid", false }
            });
        }
        actualAreaMm2 = totalArea;
        double tolerance = Math.Max(1.0e-4, expected * 1.0e-5);
        bool matches = IsFinite(totalArea) && Math.Abs(totalArea - expected) <= tolerance;
        evidence.Add("status", matches ? "VERIFIED" : "MISMATCH");
        evidence.Add("expected_open_or_closed_profile_surface_area_mm2", expected);
        evidence.Add("actual_native_sheet_face_area_mm2", totalArea);
        evidence.Add("difference_mm2", totalArea - expected);
        evidence.Add("tolerance_mm2", tolerance);
        evidence.Add("active_profile_curve_length_sum_mm", totalCurveLength);
        evidence.Add("extrusion_span_mm", spanMm);
        evidence.Add("sheet_bodies", bodyAreas);
        evidence.Add("area_oracle", "sum(active Section curve lengths) * finite extrusion span; native face area independently measured");
        return matches;
    }

    private bool VerifyF12ProfileAnalysis(Part work, Sketch sketch,
        Dictionary<string, object> expected, out Dictionary<string, object> actual,
        out Dictionary<string, object> cleanupEvidence)
    {
        actual = null;
        cleanupEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (work == null || sketch == null || expected == null)
        {
            cleanupEvidence.Add("status", "MISMATCH");
            cleanupEvidence.Add("reason", "F12 profile verification input is unavailable");
            return false;
        }
        bool modifiedBefore = work.IsModified;
        string geometryBefore = CaptureFullGeometry(work);
        try { actual = V2SketchProfileAnalyzer.AnalyzeF12WholeSketchSection(work, sketch); }
        catch (V2SketchProfileAnalyzer.SectionCleanupException ex)
        {
            cleanupEvidence.Add("status", "UNKNOWN");
            cleanupEvidence.Add("exception", ex.GetType().Name);
            throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                "F12 temporary Section cleanup could not prove the source Work Part was restored");
        }
        catch (Exception ex)
        {
            cleanupEvidence.Add("analysis_exception", ex.GetType().Name);
        }
        bool modifiedAfter;
        string geometryAfter;
        try
        {
            modifiedAfter = work.IsModified;
            geometryAfter = CaptureFullGeometry(work);
        }
        catch (Exception ex)
        {
            cleanupEvidence.Add("status", "UNKNOWN");
            cleanupEvidence.Add("post_analysis_read_exception", ex.GetType().Name);
            throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                "F12 could not verify model state after temporary Section analysis");
        }
        bool stateRestored = modifiedBefore == modifiedAfter &&
            String.Equals(geometryBefore, geometryAfter, StringComparison.Ordinal);
        cleanupEvidence.Add("part_modified_before", modifiedBefore);
        cleanupEvidence.Add("part_modified_after", modifiedAfter);
        cleanupEvidence.Add("full_sketch_geometry_restored", String.Equals(geometryBefore, geometryAfter, StringComparison.Ordinal));
        cleanupEvidence.Add("temporary_section_state_restored", stateRestored);
        bool domainMatches = actual != null && F12SectionAnalysisVerified(actual) && stateRestored;
        if (domainMatches)
        {
            string[] fields = new string[] { "input_geometry_domain_fingerprint", "section_output_domain_fingerprint",
                "open_component_count", "valid_closed_component_count", "section_loop_count" };
            foreach (string field in fields)
            {
                object left, right;
                if (!expected.TryGetValue(field, out left) || !actual.TryGetValue(field, out right) ||
                    !String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal))
                { domainMatches = false; break; }
            }
        }
        cleanupEvidence.Add("status", domainMatches ? "VERIFIED" : "MISMATCH");
        cleanupEvidence.Add("analysis", actual);
        return domainMatches;
    }

    private static bool F12PreExistingModelStatePreserved(Dictionary<string, object> before,
        Dictionary<string, object> after, M3PlanState plan, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        string newFeatureId = plan == null ? null : plan.FeatureId;
        if (before == null || after == null || String.IsNullOrEmpty(newFeatureId))
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "F12 semantic snapshots or committed Extrude feature id are unavailable");
            return false;
        }

        bool partMatches = ExactProjection(before, after, "part", new string[] { "is_modified" });
        bool identityMatches = ExactProjection(before, after, "identity", new string[] { "is_modified", "support_summary" });
        bool sessionPartsMatch = ExactSessionParts(before, after);
        bool sketchesMatch = ExactProjection(before, after, "sketches", null);
        bool supportMatches = ExactProjection(before, after, "support", null);
        bool supportSummaryMatches = ExactProjection(before, after, "support_summary", new string[] { "history_feature_count" });
        bool helicalDefinitionsMatch = ExactProjection(before, after, "native_helical_definitions", null);
        List<object> expectedNewExpressions;
        HashSet<string> expectedNewExpressionNames;
        Dictionary<string, object> expressionParameterEvidence = plan.RevolveRequest != null
            ? F13ReadExpectedRevolveExpressions(after, plan, out expectedNewExpressions, out expectedNewExpressionNames)
            : F12ReadExpectedExtrudeExpressions(after, plan, out expectedNewExpressions, out expectedNewExpressionNames);
        bool expressionParametersVerified = String.Equals(Convert.ToString(expressionParameterEvidence["status"],
            CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal);
        bool expressionsPreserved;
        List<object> newExpressions;
        Dictionary<string, object> expressionEvidence = PreserveExpressionRecords(before, after,
            expectedNewExpressions, out expressionsPreserved, out newExpressions);
        bool featuresPreserved;
        Dictionary<string, object> featureEvidence = PreserveFeatureRecords(before, after, plan,
            out featuresPreserved);
        bool dependenciesPreserved;
        Dictionary<string, object> dependencyEvidence = PreserveDependencyRecords(before, after,
            plan, expectedNewExpressionNames, out dependenciesPreserved);
        bool existingCurveIdsPreserved = PreserveExistingCurveIds(before, after);

        evidence.Add("status", partMatches && identityMatches && sessionPartsMatch && sketchesMatch &&
            supportMatches && supportSummaryMatches && helicalDefinitionsMatch && expressionParametersVerified &&
            expressionsPreserved && featuresPreserved &&
            dependenciesPreserved && existingCurveIdsPreserved ? "VERIFIED" : "MISMATCH");
        evidence.Add("part_identity_matches_except_dirty", partMatches);
        evidence.Add("binding_identity_matches_except_dirty", identityMatches);
        evidence.Add("session_parts_match_except_dirty", sessionPartsMatch);
        evidence.Add("all_sketch_geometry_frames_constraints_and_reference_states_match", sketchesMatch);
        evidence.Add("support_associations_match", supportMatches);
        evidence.Add("artifact_support_summary_matches_except_history_count", supportSummaryMatches);
        evidence.Add("native_helical_definitions_match", helicalDefinitionsMatch);
        evidence.Add(plan.RevolveRequest != null ? "revolve_request_parameter_expressions" : "extrude_request_parameter_expressions",
            expressionParameterEvidence);
        evidence.Add("existing_expressions_preserved", expressionEvidence);
        evidence.Add("existing_features_preserved", featureEvidence);
        evidence.Add("existing_dependencies_preserved", dependencyEvidence);
        evidence.Add("existing_part_curves_preserved", existingCurveIdsPreserved);
        evidence.Add("new_feature_id", newFeatureId);
        evidence.Add("new_expressions", newExpressions);
        return String.Equals(Convert.ToString(evidence["status"], CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal);
    }

    private static Dictionary<string, object> F12ReadExpectedExtrudeExpressions(Dictionary<string, object> after,
        M3PlanState plan, out List<object> expectedRows, out HashSet<string> expectedNames)
    {
        expectedRows = new List<object>();
        expectedNames = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (after == null || plan == null || plan.ExtrudeRequest == null || plan.Feature == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "request, committed Extrude feature, or after snapshot is unavailable");
            return evidence;
        }

        NXOpen.Features.ExtrudeBuilder builder = plan.Feature.OwningPart.Features.CreateExtrudeBuilder(plan.Feature);
        try
        {
            M3ExtrudeRequest request = plan.ExtrudeRequest;
            NXOpen.GeometricUtilities.Limits limits = builder.Limits;
            NXOpen.GeometricUtilities.Extend start = limits == null ? null : limits.StartExtend;
            NXOpen.GeometricUtilities.Extend end = limits == null ? null : limits.EndExtend;
            Expression startExpression = start == null ? null : start.Value;
            Expression endExpression = end == null ? null : end.Value;
            bool finite = String.Equals(request.Extent, "finite", StringComparison.Ordinal);
            bool extentTypeMatches = finite
                ? end != null && end.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value
                : end != null && end.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.ThroughAll;
            bool startMatches = start != null && start.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value &&
                F12ParameterExpressionMatches(startExpression, request.StartOffsetMm, "mm");
            bool endMatches = !finite || F12ParameterExpressionMatches(endExpression, request.EndOffsetMm, "mm");
            if (startExpression != null && !String.IsNullOrEmpty(startExpression.Name))
                expectedNames.Add(startExpression.Name.Normalize(System.Text.NormalizationForm.FormC));
            if (finite && endExpression != null && !String.IsNullOrEmpty(endExpression.Name))
                expectedNames.Add(endExpression.Name.Normalize(System.Text.NormalizationForm.FormC));

            Expression[] nativeFeatureExpressions = plan.Feature.GetExpressions();
            if (nativeFeatureExpressions == null) throw new InvalidOperationException("Extrude.GetExpressions returned null");
            Dictionary<string, Expression> nativeByName = new Dictionary<string, Expression>(StringComparer.Ordinal);
            bool featureMembershipMatches = true;
            foreach (Expression expression in nativeFeatureExpressions)
            {
                if (expression == null || String.IsNullOrEmpty(expression.Name))
                {
                    featureMembershipMatches = false;
                    continue;
                }
                string name = expression.Name.Normalize(System.Text.NormalizationForm.FormC);
                if (nativeByName.ContainsKey(name)) featureMembershipMatches = false;
                else nativeByName.Add(name, expression);
            }
            featureMembershipMatches = featureMembershipMatches && nativeByName.Count == expectedNames.Count &&
                SetEquals(new HashSet<string>(nativeByName.Keys, StringComparer.Ordinal), expectedNames);

            Dictionary<string, Dictionary<string, object>> snapshotByName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            bool snapshotMembershipMatches = true;
            foreach (object value in SnapshotList(after, "expressions"))
            {
                Dictionary<string, object> record = value as Dictionary<string, object>;
                object rawName;
                if (record == null || !record.TryGetValue("name", out rawName) || rawName == null)
                {
                    snapshotMembershipMatches = false;
                    continue;
                }
                string name = Convert.ToString(rawName, CultureInfo.InvariantCulture).Normalize(System.Text.NormalizationForm.FormC);
                if (snapshotByName.ContainsKey(name)) snapshotMembershipMatches = false;
                else snapshotByName.Add(name, record);
            }

            bool snapshotRowsMatch = true;
            foreach (string name in expectedNames)
            {
                Expression native;
                Dictionary<string, object> snapshotRecord;
                if (!nativeByName.TryGetValue(name, out native) || !snapshotByName.TryGetValue(name, out snapshotRecord))
                {
                    snapshotRowsMatch = false;
                    continue;
                }
                Dictionary<string, object> nativeRecord = F12ExpressionSnapshot(native);
                if (!String.Equals(M2Json.CanonicalText(nativeRecord), M2Json.CanonicalText(snapshotRecord), StringComparison.Ordinal))
                    snapshotRowsMatch = false;
                expectedRows.Add(nativeRecord);
            }
            expectedRows.Sort(delegate(object left, object right)
            {
                Dictionary<string, object> a = left as Dictionary<string, object>;
                Dictionary<string, object> b = right as Dictionary<string, object>;
                return StringComparer.Ordinal.Compare(Convert.ToString(a["name"], CultureInfo.InvariantCulture),
                    Convert.ToString(b["name"], CultureInfo.InvariantCulture));
            });

            NXOpen.GeometricUtilities.MultiDraft draft = builder.Draft;
            NXOpen.GeometricUtilities.FeatureOffset offset = builder.Offset;
            bool optionsMatch = draft != null && offset != null &&
                String.Equals(draft.DraftOption.ToString(), "NoDraft", StringComparison.Ordinal) &&
                String.Equals(offset.Option.ToString(), "NoOffset", StringComparison.Ordinal);
            bool dormantParametersMatch = draft != null && offset != null &&
                F12ParameterExpressionMatches(draft.DraftAngle, 0.0, "deg") &&
                F12ParameterExpressionMatches(draft.FrontDraftAngle, 0.0, "deg") &&
                F12ParameterExpressionMatches(draft.BackDraftAngle, 0.0, "deg") &&
                F12ParameterExpressionMatches(offset.StartOffset, 0.0, "mm") &&
                F12ParameterExpressionMatches(offset.EndOffset, 0.0, "mm");
            bool verified = startMatches && endMatches && extentTypeMatches && optionsMatch &&
                featureMembershipMatches && snapshotMembershipMatches && snapshotRowsMatch &&
                expectedRows.Count == expectedNames.Count && expectedNames.Count == (finite ? 2 : 1);
            evidence.Add("status", verified ? "VERIFIED" : "MISMATCH");
            evidence.Add("expected_active_parameter_names", StringListAsObjects(SortedStrings(expectedNames)));
            evidence.Add("native_feature_expression_names", StringListAsObjects(SortedStrings(nativeByName.Keys)));
            evidence.Add("native_feature_expression_membership_matches_request", featureMembershipMatches);
            evidence.Add("snapshot_expression_rows_match_native", snapshotRowsMatch && snapshotMembershipMatches);
            evidence.Add("start_parameter_matches_request", startMatches);
            evidence.Add("end_parameter_matches_request", endMatches);
            evidence.Add("extent_trim_type_matches_request", extentTypeMatches);
            evidence.Add("draft_and_offset_options_match", optionsMatch);
            evidence.Add("dormant_parameters_are_zero_after_reconstruction", dormantParametersMatch);
            evidence.Add("dormant_parameter_readback_is_diagnostic_not_feature_membership", true);
            evidence.Add("expected_expression_records", expectedRows);
            return evidence;
        }
        finally
        {
            try { builder.Destroy(); }
            catch (Exception ex)
            {
                throw M3AnalysisStateUnknown("NX Extrude expression readback builder cleanup could not be verified", ex);
            }
        }
    }

    private static bool F12ParameterExpressionMatches(Expression expression, double expected, string unitSymbol)
    {
        if (expression == null || expression.Units == null || !IsFinite(expected) ||
            !String.Equals(expression.Units.Symbol, unitSymbol, StringComparison.Ordinal) ||
            Double.IsNaN(expression.Value) || Double.IsInfinity(expression.Value)) return false;
        double rhs;
        if (!Double.TryParse(expression.RightHandSide, NumberStyles.Float, CultureInfo.InvariantCulture, out rhs) ||
            Double.IsNaN(rhs) || Double.IsInfinity(rhs)) return false;
        double canonicalExpected = expected == 0.0 ? 0.0 : expected;
        double canonicalRhs = rhs == 0.0 ? 0.0 : rhs;
        double canonicalValue = expression.Value == 0.0 ? 0.0 : expression.Value;
        return String.Equals(NumberText(canonicalExpected), NumberText(canonicalRhs), StringComparison.Ordinal) &&
            String.Equals(NumberText(canonicalExpected), NumberText(canonicalValue), StringComparison.Ordinal);
    }

    private static List<string> SortedStrings(IEnumerable<string> values)
    {
        List<string> result = new List<string>();
        foreach (string value in values ?? new string[0]) result.Add(value);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static Dictionary<string, object> F12ExpressionSnapshot(Expression expression)
    {
        if (expression == null || String.IsNullOrEmpty(expression.Name))
            throw new InvalidOperationException("F12 Extrude expression has no name");
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record.Add("name", expression.Name.Normalize(System.Text.NormalizationForm.FormC));
        record.Add("right_hand_side", expression.RightHandSide);
        record.Add("status", expression.Status.ToString());
        record.Add("value", NumberText(expression.Value));
        record.Add("unit", expression.Units == null ? null : expression.Units.Symbol);
        record.Add("journal_id", expression.JournalIdentifier);
        record.Add("flags", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "is_measurement_expression", expression.IsMeasurementExpression },
            { "is_geometric_expression", expression.IsGeometricExpression },
            { "is_no_edit", expression.IsNoEdit },
            { "is_no_update", expression.IsNoUpdate },
            { "is_rhs_locked_from_edit", expression.IsRightHandSideLockedFromEdit },
            { "is_user_locked", expression.IsUserLocked },
            { "is_user_specified", expression.IsUserSpecified }
        });
        return record;
    }

    private static bool ExactProjection(Dictionary<string, object> before, Dictionary<string, object> after,
        string key, string[] omittedFields)
    {
        object beforeValue, afterValue;
        if (!before.TryGetValue(key, out beforeValue) || !after.TryGetValue(key, out afterValue)) return false;
        Dictionary<string, object> beforeDictionary = beforeValue as Dictionary<string, object>;
        Dictionary<string, object> afterDictionary = afterValue as Dictionary<string, object>;
        if (beforeDictionary != null || afterDictionary != null)
        {
            if (beforeDictionary == null || afterDictionary == null) return false;
            return String.Equals(M2Json.CanonicalText(OmitFields(beforeDictionary, omittedFields)),
                M2Json.CanonicalText(OmitFields(afterDictionary, omittedFields)), StringComparison.Ordinal);
        }
        List<object> beforeList = beforeValue as List<object>;
        List<object> afterList = afterValue as List<object>;
        return beforeList != null && afterList != null &&
            String.Equals(M2Json.CanonicalText(beforeList), M2Json.CanonicalText(afterList), StringComparison.Ordinal);
    }

    private static Dictionary<string, object> OmitFields(Dictionary<string, object> source, string[] omittedFields)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> pair in source)
        {
            bool omit = false;
            if (omittedFields != null)
                for (int i = 0; i < omittedFields.Length; i++)
                    if (String.Equals(pair.Key, omittedFields[i], StringComparison.Ordinal)) { omit = true; break; }
            if (!omit) result.Add(pair.Key, pair.Value);
        }
        return result;
    }

    private static bool ExactSessionParts(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        object beforeValue, afterValue;
        if (!before.TryGetValue("session_parts", out beforeValue) || !after.TryGetValue("session_parts", out afterValue)) return false;
        List<object> beforeList = beforeValue as List<object>;
        List<object> afterList = afterValue as List<object>;
        if (beforeList == null || afterList == null || beforeList.Count != afterList.Count) return false;
        List<Dictionary<string, object>> left = new List<Dictionary<string, object>>();
        List<Dictionary<string, object>> right = new List<Dictionary<string, object>>();
        foreach (object value in beforeList)
        {
            Dictionary<string, object> item = value as Dictionary<string, object>;
            if (item == null) return false;
            left.Add(OmitFields(item, new string[] { "is_modified" }));
        }
        foreach (object value in afterList)
        {
            Dictionary<string, object> item = value as Dictionary<string, object>;
            if (item == null) return false;
            right.Add(OmitFields(item, new string[] { "is_modified" }));
        }
        left.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            return String.CompareOrdinal(Convert.ToString(a["path"], CultureInfo.InvariantCulture),
                Convert.ToString(b["path"], CultureInfo.InvariantCulture));
        });
        right.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            return String.CompareOrdinal(Convert.ToString(a["path"], CultureInfo.InvariantCulture),
                Convert.ToString(b["path"], CultureInfo.InvariantCulture));
        });
        return String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal);
    }

    private static Dictionary<string, object> PreserveFeatureRecords(Dictionary<string, object> before,
        Dictionary<string, object> after, M3PlanState plan, out bool preserved)
    {
        string newFeatureId = plan == null ? null : plan.FeatureId;
        List<object> oldRows = SnapshotList(before, "features");
        List<object> newRows = SnapshotList(after, "features");
        Dictionary<string, Dictionary<string, object>> newById = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (object value in newRows)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record == null || !(record["journal_id"] is string) || newById.ContainsKey((string)record["journal_id"]))
            {
                preserved = false;
                return new Dictionary<string, object>(StringComparer.Ordinal) { { "status", "MISMATCH" }, { "reason", "post-commit feature rows have missing/duplicate ids" } };
            }
            newById.Add((string)record["journal_id"], record);
        }
        Dictionary<string, object> newFeatureRecord;
        if (!newById.TryGetValue(newFeatureId, out newFeatureRecord))
        {
            preserved = false;
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", "MISMATCH" }, { "reason", "new Extrude feature is absent from post-commit history" }
            };
        }
        HashSet<string> expectedParents = new HashSet<string>(StringComparer.Ordinal);
        object rawParents;
        List<object> parentList = newFeatureRecord.TryGetValue("parent_ids", out rawParents) ? rawParents as List<object> : null;
        if (parentList == null)
        {
            preserved = false;
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", "MISMATCH" }, { "reason", "new Extrude feature parent list is unavailable" }
            };
        }
        foreach (object parent in parentList) expectedParents.Add(Convert.ToString(parent, CultureInfo.InvariantCulture));
        bool sameOld = true;
        List<object> mismatchedExistingRows = new List<object>();
        List<object> nativeInverseChildEchoRows = new List<object>();
        bool nativeInverseChildEchoObservedAny = false;
        string nativeInverseChildEchoPolicy = "For an explicit precommit target-body owner that is a declared parent of this Extrude (excluding the source Sketch), accept either the unchanged prior child set or that set plus this Extrude; all prior children must remain and no other child may be added.";
        foreach (object value in oldRows)
        {
            Dictionary<string, object> oldRecord = value as Dictionary<string, object>;
            Dictionary<string, object> actual;
            if (oldRecord == null || !(oldRecord["journal_id"] is string))
            {
                sameOld = false;
                mismatchedExistingRows.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "reason", "before_feature_row_missing_or_has_no_journal_id" },
                    { "before_feature_record", oldRecord }, { "after_feature_record", null }
                });
                continue;
            }
            string featureId = (string)oldRecord["journal_id"];
            if (!newById.TryGetValue(featureId, out actual))
            {
                sameOld = false;
                mismatchedExistingRows.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "journal_id", featureId }, { "reason", "existing_feature_row_missing_after_commit" },
                    { "before_feature_record", oldRecord }, { "after_feature_record", null }
                });
                continue;
            }
            List<string> oldBodyIds = ReadStringList(oldRecord, "body_ids");
            bool targetBodyOwner = plan != null && plan.TargetBody != null && oldBodyIds.Contains(plan.TargetBodyId);
            string requestMode = plan == null ? null : plan.RevolveRequest != null
                ? plan.RevolveRequest.Mode : plan.ExtrudeRequest == null ? null : plan.ExtrudeRequest.Mode;
            bool explicitTargetBodyRequest = plan != null && !String.IsNullOrEmpty(requestMode) &&
                !String.Equals(requestMode, "create", StringComparison.Ordinal) &&
                !String.IsNullOrEmpty(plan.TargetBodyId) && plan.TargetBody != null;
            string sourceSketchFeatureId = plan == null || plan.Sketch == null || plan.Sketch.Feature == null
                ? null : RequiredJournalId(plan.Sketch.Feature, "F12 source Sketch feature");
            bool explicitTargetBodyOwnerParent = explicitTargetBodyRequest && targetBodyOwner &&
                expectedParents.Contains(featureId) &&
                !String.Equals(featureId, sourceSketchFeatureId, StringComparison.Ordinal);
            Dictionary<string, object> oldCore = OmitFields(oldRecord,
                targetBodyOwner ? new string[] { "child_ids", "body_ids" } : new string[] { "child_ids" });
            Dictionary<string, object> actualCore = OmitFields(actual,
                targetBodyOwner ? new string[] { "child_ids", "body_ids" } : new string[] { "child_ids" });
            List<string> oldChildren = ReadStringList(oldRecord, "child_ids");
            oldChildren.Sort(StringComparer.Ordinal);
            List<string> expectedChildren = new List<string>(oldChildren);
            if (expectedParents.Contains(featureId) && !expectedChildren.Contains(newFeatureId)) expectedChildren.Add(newFeatureId);
            expectedChildren.Sort(StringComparer.Ordinal);
            List<string> actualChildren = ReadStringList(actual, "child_ids");
            actualChildren.Sort(StringComparer.Ordinal);
            bool actualChildrenMatchOld = String.Equals(M2Json.CanonicalText(StringListAsObjects(oldChildren)),
                M2Json.CanonicalText(StringListAsObjects(actualChildren)), StringComparison.Ordinal);
            bool actualChildrenMatchExpected = String.Equals(M2Json.CanonicalText(StringListAsObjects(expectedChildren)),
                M2Json.CanonicalText(StringListAsObjects(actualChildren)), StringComparison.Ordinal);
            bool nativeInverseChildEchoObservedForRow = explicitTargetBodyOwnerParent &&
                !actualChildrenMatchExpected && actualChildrenMatchOld;
            string nativeInverseChildEchoAllowedReason = explicitTargetBodyOwnerParent ? nativeInverseChildEchoPolicy : null;
            if (explicitTargetBodyOwnerParent)
            {
                nativeInverseChildEchoObservedAny = nativeInverseChildEchoObservedAny || nativeInverseChildEchoObservedForRow;
                nativeInverseChildEchoRows.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "journal_id", featureId },
                    { "explicit_target_body_owner_declared_parent", true },
                    { "native_inverse_child_echo_observed", nativeInverseChildEchoObservedForRow },
                    { "native_inverse_child_echo_allowed_reason", nativeInverseChildEchoAllowedReason },
                    { "before_child_ids", StringListAsObjects(oldChildren) },
                    { "expected_child_ids_after_declared_new_feature", StringListAsObjects(expectedChildren) },
                    { "actual_child_ids", StringListAsObjects(actualChildren) }
                });
            }
            bool bodiesPreserved = true;
            List<string> removedNonTargetBodyIds = new List<string>();
            List<string> unexpectedActualBodyIds = new List<string>();
            HashSet<string> allowedBodyIds = new HashSet<string>(StringComparer.Ordinal);
            if (targetBodyOwner)
            {
                foreach (string oldBodyId in oldBodyIds) allowedBodyIds.Add(oldBodyId);
                if (plan.AffectedBodyIds != null)
                    foreach (string bodyId in plan.AffectedBodyIds) allowedBodyIds.Add(bodyId);
                List<string> actualBodyIds = ReadStringList(actual, "body_ids");
                HashSet<string> actualBodySet = new HashSet<string>(actualBodyIds, StringComparer.Ordinal);
                foreach (string oldBodyId in oldBodyIds)
                    if (!String.Equals(oldBodyId, plan.TargetBodyId, StringComparison.Ordinal) && !actualBodySet.Contains(oldBodyId))
                    { bodiesPreserved = false; removedNonTargetBodyIds.Add(oldBodyId); }
                foreach (string actualBodyId in actualBodyIds)
                    if (!allowedBodyIds.Contains(actualBodyId))
                    { bodiesPreserved = false; unexpectedActualBodyIds.Add(actualBodyId); }
            }
            bool corePreserved = String.Equals(M2Json.CanonicalText(oldCore), M2Json.CanonicalText(actualCore), StringComparison.Ordinal);
            bool childrenPreserved = explicitTargetBodyOwnerParent
                ? actualChildrenMatchOld || actualChildrenMatchExpected
                : actualChildrenMatchExpected;
            if (!corePreserved || !childrenPreserved || !bodiesPreserved)
            {
                sameOld = false;
                List<object> reasons = new List<object>();
                if (!corePreserved) reasons.Add("non_child_non_body_feature_projection_changed");
                if (!childrenPreserved) reasons.Add("child_ids_differ_from_existing_plus_declared_new_child");
                if (!bodiesPreserved) reasons.Add("target_owner_body_ids_outside_current_journal_id_projection");
                Dictionary<string, object> mismatch = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "journal_id", featureId }, { "mismatch_reasons", reasons },
                    { "target_body_owner_by_precommit_requested_id", targetBodyOwner },
                    { "before_feature_record", oldRecord }, { "after_feature_record", actual },
                    { "before_feature_projection", oldCore }, { "after_feature_projection", actualCore },
                    { "expected_child_ids_after_declared_new_feature", StringListAsObjects(expectedChildren) },
                    { "actual_child_ids", StringListAsObjects(actualChildren) },
                    { "core_projection_preserved", corePreserved }, { "child_ids_preserved", childrenPreserved },
                    { "native_inverse_child_echo_observed", nativeInverseChildEchoObservedForRow },
                    { "native_inverse_child_echo_allowed_reason", nativeInverseChildEchoAllowedReason },
                    { "body_ids_preserved_by_current_projection", bodiesPreserved },
                    { "body_ids_before", StringListAsObjects(SortedStrings(oldBodyIds)) },
                    { "body_ids_after", StringListAsObjects(SortedStrings(ReadStringList(actual, "body_ids"))) },
                    { "current_allowed_body_ids_by_journal_id", StringListAsObjects(SortedStrings(allowedBodyIds)) },
                    { "removed_non_target_body_ids", StringListAsObjects(SortedStrings(removedNonTargetBodyIds)) },
                    { "unexpected_actual_body_ids", StringListAsObjects(SortedStrings(unexpectedActualBodyIds)) },
                    { "body_lineage_by_native_tag_and_explicit_request", F12FeatureBodyLineageEvidence(plan,
                        oldBodyIds, ReadStringList(actual, "body_ids")) }
                };
                mismatchedExistingRows.Add(mismatch);
            }
        }
        List<string> newIds = new List<string>();
        foreach (KeyValuePair<string, Dictionary<string, object>> item in newById)
            if (!ContainsFeatureId(oldRows, item.Key)) newIds.Add(item.Key);
        newIds.Sort(StringComparer.Ordinal);
        preserved = sameOld && newIds.Count == 1 && String.Equals(newIds[0], newFeatureId, StringComparison.Ordinal);
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", preserved ? "VERIFIED" : "MISMATCH" },
            { "before_feature_count", oldRows.Count }, { "after_feature_count", newRows.Count },
            { "new_feature_ids", StringListAsObjects(newIds) }, { "expected_new_feature_id", newFeatureId },
            { "existing_feature_records_preserved_except_new_child_and_selected_target_body_lineage", sameOld },
            { "mismatched_existing_rows", mismatchedExistingRows },
            { "native_inverse_child_echo_observed", nativeInverseChildEchoObservedAny },
            { "native_inverse_child_echo_allowed_reason", nativeInverseChildEchoPolicy },
            { "native_inverse_child_echo_diagnostics", nativeInverseChildEchoRows },
            { "mismatch_diagnostics_only", true }
        };
    }

    private static Dictionary<string, object> F12FeatureBodyLineageEvidence(M3PlanState plan,
        List<string> beforeBodyIds, List<string> afterBodyIds)
    {
        string requestMode = plan == null ? null : plan.RevolveRequest != null
            ? plan.RevolveRequest.Mode : plan.ExtrudeRequest == null ? null : plan.ExtrudeRequest.Mode;
        string targetId = plan == null ? null : plan.TargetBodyId;
        string targetTag = plan == null || plan.TargetBody == null ? null : plan.TargetBody.Tag.ToString();
        HashSet<string> resultTags = new HashSet<string>(StringComparer.Ordinal);
        List<object> resultBodies = new List<object>();
        if (plan != null && plan.AffectedBodies != null)
        {
            foreach (M3BodyMeasure body in plan.AffectedBodies)
            {
                if (body == null || body.Body == null) continue;
                string tag = body.Body.Tag.ToString();
                resultTags.Add(tag);
                resultBodies.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "body_id", body.Id }, { "body_tag", tag }
                });
            }
        }
        List<object> beforeMembers = F12BodyMembershipTagRecords(beforeBodyIds, plan == null ? null : plan.BodiesBefore);
        List<object> afterMembers = F12BodyMembershipTagRecords(afterBodyIds, plan == null ? null : plan.BodiesAfter);
        List<object> beforeInventory = F12BodyLineageInventory(plan == null ? null : plan.BodiesBefore,
            beforeBodyIds, targetId, targetTag, resultTags);
        List<object> afterInventory = F12BodyLineageInventory(plan == null ? null : plan.BodiesAfter,
            afterBodyIds, targetId, targetTag, resultTags);
        bool explicitTargetRequest = requestMode != null &&
            !String.Equals(requestMode, "create", StringComparison.Ordinal) &&
            !String.IsNullOrEmpty(targetId) && !String.IsNullOrEmpty(targetTag);
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "diagnostic_only_no_preservation_waiver", true },
            { "request_mode", requestMode },
            { "requested_target_body_id", targetId },
            { "requested_target_body_tag_from_plan", targetTag },
            { "explicit_non_create_target_request_present", explicitTargetRequest },
            { "lineage_basis", "native Body.Tag mapped from precommit/postcommit inventory; request permission is scoped to the explicit target body and this operation feature result" },
            { "request_scoped_candidate_target_tags", StringListAsObjects(SortedStrings(resultTagsWithTarget(resultTags, targetTag))) },
            { "operation_feature_result_bodies_by_native_tag", resultBodies },
            { "before_row_body_ids_mapped_by_native_tag", beforeMembers },
            { "after_row_body_ids_mapped_by_native_tag", afterMembers },
            { "before_inventory_target_or_row_or_result_bodies", beforeInventory },
            { "after_inventory_target_or_row_or_result_bodies", afterInventory }
        };
    }

    private static HashSet<string> resultTagsWithTarget(HashSet<string> resultTags, string targetTag)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        if (resultTags != null) foreach (string tag in resultTags) tags.Add(tag);
        if (!String.IsNullOrEmpty(targetTag)) tags.Add(targetTag);
        return tags;
    }

    private static List<object> F12BodyMembershipTagRecords(List<string> bodyIds, List<M3BodyMeasure> inventory)
    {
        List<object> records = new List<object>();
        foreach (string bodyId in bodyIds ?? new List<string>())
        {
            List<string> tags = new List<string>();
            if (inventory != null)
                foreach (M3BodyMeasure body in inventory)
                    if (body != null && body.Body != null && String.Equals(body.Id, bodyId, StringComparison.Ordinal))
                        tags.Add(body.Body.Tag.ToString());
            tags.Sort(StringComparer.Ordinal);
            records.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "body_id", bodyId }, { "mapped_body_tags", StringListAsObjects(tags) },
                { "mapping_status", tags.Count == 1 ? "MAPPED" : (tags.Count == 0 ? "UNMAPPED" : "AMBIGUOUS") }
            });
        }
        return records;
    }

    private static List<object> F12BodyLineageInventory(List<M3BodyMeasure> inventory, List<string> rowBodyIds,
        string targetId, string targetTag, HashSet<string> resultTags)
    {
        List<object> records = new List<object>();
        if (inventory == null) return records;
        HashSet<string> rowIds = new HashSet<string>(rowBodyIds ?? new List<string>(), StringComparer.Ordinal);
        foreach (M3BodyMeasure body in inventory)
        {
            if (body == null || body.Body == null) continue;
            string tag = body.Body.Tag.ToString();
            bool targetIdMatch = String.Equals(body.Id, targetId, StringComparison.Ordinal);
            bool targetTagMatch = String.Equals(tag, targetTag, StringComparison.Ordinal);
            bool resultTagMatch = resultTags != null && resultTags.Contains(tag);
            bool rowMember = rowIds.Contains(body.Id);
            if (!targetIdMatch && !targetTagMatch && !resultTagMatch && !rowMember) continue;
            records.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "body_id", body.Id }, { "body_tag", tag }, { "is_row_member_by_journal_id", rowMember },
                { "matches_requested_target_id", targetIdMatch }, { "matches_requested_target_tag", targetTagMatch },
                { "matches_new_extrude_result_tag", resultTagMatch }
            });
        }
        return records;
    }

    private static List<string> ReadStringList(Dictionary<string, object> record, string key)
    {
        object value;
        List<object> input = record != null && record.TryGetValue(key, out value) ? value as List<object> : null;
        if (input == null) return new List<string>();
        List<string> result = new List<string>();
        foreach (object item in input) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
        return result;
    }

    private static bool SetEquals(HashSet<string> left, HashSet<string> right)
    {
        return left != null && right != null && left.SetEquals(right);
    }

    private static Dictionary<string, object> FindSnapshotFeature(Dictionary<string, object> snapshot, string featureId)
    {
        if (snapshot == null || String.IsNullOrEmpty(featureId)) return null;
        foreach (object value in SnapshotList(snapshot, "features"))
        {
            Dictionary<string, object> feature = value as Dictionary<string, object>;
            object id;
            if (feature != null && feature.TryGetValue("journal_id", out id) &&
                String.Equals(Convert.ToString(id, CultureInfo.InvariantCulture), featureId, StringComparison.Ordinal))
                return feature;
        }
        return null;
    }

    private static HashSet<string> F12AllowedParentFeatureIds(Dictionary<string, object> before, M3PlanState plan)
    {
        HashSet<string> allowed = new HashSet<string>(StringComparer.Ordinal);
        if (plan != null && plan.Sketch != null && plan.Sketch.Feature != null)
            allowed.Add(RequiredJournalId(plan.Sketch.Feature, "F12 source Sketch feature"));
        if (plan != null && !String.IsNullOrEmpty(plan.TargetBodyId))
        {
            foreach (object value in SnapshotList(before, "features"))
            {
                Dictionary<string, object> feature = value as Dictionary<string, object>;
                if (feature == null) continue;
                object id;
                if (!(feature.TryGetValue("journal_id", out id) && id is string)) continue;
                List<string> bodyIds = ReadStringList(feature, "body_ids");
                if (bodyIds.Contains(plan.TargetBodyId)) allowed.Add((string)id);
            }
        }
        return allowed;
    }

    private static bool ContainsFeatureId(List<object> rows, string id)
    {
        foreach (object value in rows)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            object raw;
            if (record != null && record.TryGetValue("journal_id", out raw) &&
                String.Equals(Convert.ToString(raw, CultureInfo.InvariantCulture), id, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static Dictionary<string, object> PreserveDependencyRecords(Dictionary<string, object> before,
        Dictionary<string, object> after, M3PlanState plan, HashSet<string> expectedExpressionNames,
        out bool preserved)
    {
        string newFeatureId = plan == null ? null : plan.FeatureId;
        List<object> oldRows = SnapshotList(before, "dependencies");
        List<object> newRows = SnapshotList(after, "dependencies");
        Dictionary<string, object> newFeature = FindSnapshotFeature(after, newFeatureId);
        List<string> declaredParentList = ReadStringList(newFeature, "parent_ids");
        List<string> declaredExpressionList = ReadStringList(newFeature, "expression_ids");
        HashSet<string> declaredParents = new HashSet<string>(declaredParentList, StringComparer.Ordinal);
        HashSet<string> declaredExpressions = new HashSet<string>(declaredExpressionList, StringComparer.Ordinal);
        HashSet<string> allowedParents = F12AllowedParentFeatureIds(before, plan);
        bool parentWhitelistValid = declaredParentList.Count == declaredParents.Count;
        bool expressionMembershipValid = expectedExpressionNames != null &&
            declaredExpressionList.Count == declaredExpressions.Count && SetEquals(declaredExpressions, expectedExpressionNames);
        string sketchFeatureId = plan == null || plan.Sketch == null || plan.Sketch.Feature == null
            ? null : RequiredJournalId(plan.Sketch.Feature, "F12 source Sketch feature");
        bool sourceSketchParent = !String.IsNullOrEmpty(sketchFeatureId) && declaredParents.Contains(sketchFeatureId);
        if (!sourceSketchParent) parentWhitelistValid = false;
        bool targetOwnerParent = false;
        foreach (string parent in declaredParents)
        {
            if (!allowedParents.Contains(parent)) parentWhitelistValid = false;
            if (!String.Equals(parent, sketchFeatureId, StringComparison.Ordinal)) targetOwnerParent = true;
        }
        string requestMode = plan == null ? null : plan.RevolveRequest != null
            ? plan.RevolveRequest.Mode : plan.ExtrudeRequest == null ? null : plan.ExtrudeRequest.Mode;
        if (String.IsNullOrEmpty(requestMode)) parentWhitelistValid = false;
        else if (String.Equals(requestMode, "create", StringComparison.Ordinal))
            parentWhitelistValid = parentWhitelistValid && declaredParents.Count == 1;
        else
            parentWhitelistValid = parentWhitelistValid && targetOwnerParent;
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object value in newRows)
        {
            string canonical = M2Json.CanonicalText(value);
            int count;
            counts.TryGetValue(canonical, out count);
            counts[canonical] = count + 1;
        }
        bool oldEdgesPreserved = true;
        foreach (object value in oldRows)
        {
            string canonical = M2Json.CanonicalText(value);
            int count;
            if (!counts.TryGetValue(canonical, out count) || count <= 0) { oldEdgesPreserved = false; continue; }
            counts[canonical] = count - 1;
        }
        List<object> unexpected = new List<object>();
        HashSet<string> newParentEdges = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> newExpressionEdges = new HashSet<string>(StringComparer.Ordinal);
        int newParentEdgeCount = 0;
        int newExpressionEdgeCount = 0;
        foreach (KeyValuePair<string, int> item in counts)
        {
            for (int i = 0; i < item.Value; i++)
            {
                Dictionary<string, object> edge = null;
                foreach (object value in newRows)
                    if (String.Equals(M2Json.CanonicalText(value), item.Key, StringComparison.Ordinal))
                    { edge = value as Dictionary<string, object>; break; }
                object kindValue, sourceValue, targetValue;
                string kind = edge != null && edge.TryGetValue("kind", out kindValue)
                    ? Convert.ToString(kindValue, CultureInfo.InvariantCulture) : null;
                string source = edge != null && edge.TryGetValue("source_id", out sourceValue)
                    ? Convert.ToString(sourceValue, CultureInfo.InvariantCulture) : null;
                string target = edge != null && edge.TryGetValue("target_id", out targetValue)
                    ? Convert.ToString(targetValue, CultureInfo.InvariantCulture) : null;
                if (String.Equals(kind, "FEATURE_PARENT", StringComparison.Ordinal) &&
                    String.Equals(target, newFeatureId, StringComparison.Ordinal) && declaredParents.Contains(source))
                { newParentEdges.Add(source); newParentEdgeCount++; }
                else if (String.Equals(kind, "FEATURE_EXPRESSION", StringComparison.Ordinal) &&
                    String.Equals(source, newFeatureId, StringComparison.Ordinal) && declaredExpressions.Contains(target))
                { newExpressionEdges.Add(target); newExpressionEdgeCount++; }
                else unexpected.Add(edge ?? (object)item.Key);
            }
        }
        preserved = oldEdgesPreserved && unexpected.Count == 0 && parentWhitelistValid && expressionMembershipValid &&
            SetEquals(newParentEdges, declaredParents) && SetEquals(newExpressionEdges, declaredExpressions) &&
            newParentEdgeCount == declaredParents.Count && newExpressionEdgeCount == declaredExpressions.Count;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", preserved ? "VERIFIED" : "MISMATCH" },
            { "before_dependency_count", oldRows.Count }, { "after_dependency_count", newRows.Count },
            { "existing_edges_preserved", oldEdgesPreserved }, { "unexpected_new_edges", unexpected },
            { "new_feature_declared_parent_ids", StringListAsObjects(SortedStrings(declaredParents)) },
            { "allowed_parent_ids", StringListAsObjects(SortedStrings(allowedParents)) },
            { "source_sketch_parent_present", sourceSketchParent },
            { "explicit_target_body_owner_parent_present", targetOwnerParent },
            { "new_feature_declared_expression_ids", StringListAsObjects(SortedStrings(declaredExpressions)) },
            { "expected_active_expression_ids", expectedExpressionNames == null ? new List<object>() : StringListAsObjects(SortedStrings(expectedExpressionNames)) },
            { "new_feature_expression_membership_matches_request_parameters", expressionMembershipValid },
            { "new_feature_parent_edge_count", newParentEdgeCount },
            { "new_feature_expression_edge_count", newExpressionEdgeCount },
            { "new_feature_parent_edges_match", SetEquals(newParentEdges, declaredParents) },
            { "new_feature_expression_edges_match", SetEquals(newExpressionEdges, declaredExpressions) }
        };
    }

    private static Dictionary<string, object> PreserveExpressionRecords(Dictionary<string, object> before,
        Dictionary<string, object> after, List<object> expectedNewExpressions,
        out bool preserved, out List<object> newExpressions)
    {
        List<object> oldRows = SnapshotList(before, "expressions");
        List<object> newRows = SnapshotList(after, "expressions");
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object value in newRows)
        {
            string canonical = M2Json.CanonicalText(value);
            int count;
            counts.TryGetValue(canonical, out count);
            counts[canonical] = count + 1;
        }
        bool oldExpressionsPreserved = true;
        foreach (object value in oldRows)
        {
            string canonical = M2Json.CanonicalText(value);
            int count;
            if (!counts.TryGetValue(canonical, out count) || count <= 0) { oldExpressionsPreserved = false; continue; }
            counts[canonical] = count - 1;
        }
        List<object> additions = new List<object>();
        foreach (KeyValuePair<string, int> item in counts)
            for (int i = 0; i < item.Value; i++)
            {
                object found = null;
                foreach (object value in newRows)
                    if (String.Equals(M2Json.CanonicalText(value), item.Key, StringComparison.Ordinal)) { found = value; break; }
                if (found != null) additions.Add(found);
            }
        Dictionary<string, int> expectedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object value in expectedNewExpressions ?? new List<object>())
        {
            string canonical = M2Json.CanonicalText(value);
            int count;
            expectedCounts.TryGetValue(canonical, out count);
            expectedCounts[canonical] = count + 1;
        }
        Dictionary<string, int> additionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object value in additions)
        {
            string canonical = M2Json.CanonicalText(value);
            int count;
            additionCounts.TryGetValue(canonical, out count);
            additionCounts[canonical] = count + 1;
        }
        bool additionsAllowed = expectedNewExpressions != null && expectedCounts.Count == additionCounts.Count;
        if (additionsAllowed)
            foreach (KeyValuePair<string, int> item in expectedCounts)
            {
                int actualCount;
                if (!additionCounts.TryGetValue(item.Key, out actualCount) || actualCount != item.Value)
                { additionsAllowed = false; break; }
            }
        preserved = oldExpressionsPreserved && additionsAllowed;
        newExpressions = additions;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", preserved ? "VERIFIED" : "MISMATCH" },
            { "before_expression_count", oldRows.Count }, { "after_expression_count", newRows.Count },
            { "existing_expressions_exact", oldExpressionsPreserved },
            { "new_expressions_match_request_derived_native_parameters", additionsAllowed },
            { "expected_new_expression_count", expectedNewExpressions == null ? 0 : expectedNewExpressions.Count },
            { "new_expressions", additions }
        };
    }

    private static bool PreserveExistingCurveIds(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        HashSet<string> afterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in SnapshotList(after, "curve_ids")) afterIds.Add(Convert.ToString(value, CultureInfo.InvariantCulture));
        foreach (object value in SnapshotList(before, "curve_ids"))
            if (!afterIds.Contains(Convert.ToString(value, CultureInfo.InvariantCulture))) return false;
        return true;
    }
}
