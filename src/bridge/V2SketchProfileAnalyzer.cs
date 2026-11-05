using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NXOpen.Utilities;

/// <summary>Read-only sketch profile checks, with temporary Section objects always rolled back.</summary>
public static class V2SketchProfileAnalyzer
{
    private const double EndpointToleranceMm = 1.0e-6;
    private const double DirectionTolerance = 1.0e-7;
    private const double FullTurn = 2.0 * Math.PI;
    private const int SamplesPerNurbsSpan = 16;
    private const int MaximumOverlapSamples = 8192;

    private sealed class CurveInfo
    {
        internal Curve Curve;
        internal string Id;
        internal double StartParameter;
        internal double EndParameter;
        internal Point3d Start;
        internal Point3d End;
        internal double Length;
        internal bool Closed;
        internal bool EndpointValid;
        internal bool Supported;
        internal bool InClosedGraphComponent;
        internal IntPtr Evaluator;
    }

    private sealed class GraphComponent
    {
        internal readonly HashSet<int> Vertices = new HashSet<int>();
        internal readonly List<int> Curves = new List<int>();
        internal bool Closed;
    }

    private sealed class ConicInfo
    {
        internal Point3d Center;
        // Vectors from the center to the native curve at parameters 0 and pi/2.
        internal Vector3d AxisA;
        internal Vector3d AxisB;
        internal double Start;
        internal double End;
    }

    private sealed class CurveMatch
    {
        internal double SourceParameter;
        internal double TargetParameter;
        internal Point3d Point;
    }

    private sealed class AngularSegment
    {
        internal double Start;
        internal double End;
    }

    internal sealed class SectionCleanupException : InvalidOperationException
    {
        internal SectionCleanupException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Builds and rolls back a whole-sketch Section, returning domain evidence.</summary>
    public static Dictionary<string, object> AnalyzeF12WholeSketchSection(Part part, Sketch sketch)
    {
        Dictionary<string, object> input = BuildF12InputDomain(part, sketch);
        string inputStatus = Convert.ToString(input["status"], CultureInfo.InvariantCulture);
        if (!String.Equals(inputStatus, "SECTION_ANALYSIS_PENDING", StringComparison.Ordinal)) return input;
        return AnalyzeF12TemporaryWholeSketchSection(part, sketch, input);
    }

    /// <summary>Reads an existing Section without changing or destroying it.</summary>
    public static Dictionary<string, object> AnalyzeF12SectionDomain(Part part, Sketch sketch, Section section)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (sketch == null) throw new ArgumentNullException("sketch");
        if (section == null) throw new ArgumentNullException("section");

        Dictionary<string, object> report = BuildF12InputDomain(part, sketch);
        if (!String.Equals(Convert.ToString(report["status"], CultureInfo.InvariantCulture),
            "SECTION_ANALYSIS_PENDING", StringComparison.Ordinal)) return report;
        try
        {
            UFSession uf = UFSession.GetUFSession();
            double mmPerPartUnit;
            if (!TryF12UnitScale(part, out mmPerPartUnit))
                throw new InvalidOperationException("Part units are not supported for F12 Section readback.");

            CurveInfo[] inputCurves;
            int openComponents, closedComponents;
            bool branch;
            string topologyFailure;
            if (!F12ReadInputTopology(sketch, uf, mmPerPartUnit,
                F12InputJournalIdSet(F12InputRecordDictionaries(report)), out inputCurves,
                out openComponents, out closedComponents, out branch, out topologyFailure))
            {
                report["status"] = "INPUT_DOMAIN_UNAVAILABLE";
                report["failure_reason"] = topologyFailure;
                report["topology_status"] = "NOT_EVALUATED_INPUT_DOMAIN_INCOMPLETE";
                return report;
            }
            report["open_component_count"] = openComponents;
            report["valid_closed_component_count"] = closedComponents;
            report["endpoint_graph_closed_component_count"] = closedComponents;
            report["endpoint_graph_branch_detected"] = branch;
            report["topology_status"] = branch ? "BRANCH_DETECTED" : "ENDPOINT_GRAPH_VERIFIED";
            report["open_curve_source_jids"] = F12ComponentJournalIds(new List<CurveInfo>(inputCurves), false);
            report["closed_curve_source_jids"] = F12ComponentJournalIds(new List<CurveInfo>(inputCurves), true);

            Dictionary<string, object> sectionInputRelation = F12ReadSectionInputEvidence(section, sketch,
                uf, mmPerPartUnit, F12InputRecordDictionaries(report));
            report["section_input_relation"] = sectionInputRelation;
            report["section_input_records"] = sectionInputRelation["section_input_records"];
            report["section_input_domain_fingerprint"] = sectionInputRelation["section_input_domain_fingerprint"];

            NXObject[] outputObjects;
            section.GetOutputCurves(out outputObjects);
            if (outputObjects == null)
                throw new InvalidOperationException("Section.GetOutputCurves returned null.");
            List<Dictionary<string, object>> sortedOutputRecords = new List<Dictionary<string, object>>();
            List<CurveInfo> outputTopologyCurves = new List<CurveInfo>();
            for (int i = 0; i < outputObjects.Length; i++)
            {
                Curve outputCurve = outputObjects[i] as Curve;
                if (outputCurve == null)
                    throw new InvalidOperationException("Section output contains a non-Curve entity.");
                double[] limits = F12AskLimits(uf, outputCurve);
                CurveInfo outputInfo = new CurveInfo();
                outputInfo.Curve = outputCurve;
                outputInfo.Id = outputCurve.JournalIdentifier;
                outputInfo.Supported = true;
                outputInfo.StartParameter = limits[0];
                outputInfo.EndParameter = limits[1];
                outputInfo.Start = F12EvaluatePoint(uf, outputCurve, limits[0]);
                outputInfo.End = F12EvaluatePoint(uf, outputCurve, limits[1]);
                outputInfo.Length = outputCurve.GetLength();
                outputInfo.Closed = Distance(outputInfo.Start, outputInfo.End) <= EndpointToleranceMm / mmPerPartUnit;
                outputInfo.EndpointValid = IsFinite(outputInfo.Start.X) && IsFinite(outputInfo.Start.Y) &&
                    IsFinite(outputInfo.Start.Z) && IsFinite(outputInfo.End.X) && IsFinite(outputInfo.End.Y) &&
                    IsFinite(outputInfo.End.Z) && IsFinite(outputInfo.Length);
                if (!outputInfo.EndpointValid || outputInfo.Length <= EndpointToleranceMm / mmPerPartUnit)
                    throw new InvalidOperationException("Section output curve endpoint or length was invalid.");
                outputTopologyCurves.Add(outputInfo);
                Dictionary<string, object> outputRecord = new Dictionary<string, object>(StringComparer.Ordinal);
                outputRecord["section_output_jid"] = outputCurve.JournalIdentifier;
                outputRecord["curve_type"] = outputCurve.GetType().Name;
                outputRecord["geometry"] = F12CurveGeometryRecord(uf, outputCurve, limits, mmPerPartUnit);
                outputRecord["geometry_fingerprint"] = F12GeometryFingerprint((Dictionary<string, object>)outputRecord["geometry"]);
                sortedOutputRecords.Add(outputRecord);
            }
            sortedOutputRecords.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
            {
                int comparison = StringComparer.Ordinal.Compare((string)left["geometry_fingerprint"],
                    (string)right["geometry_fingerprint"]);
                if (comparison != 0) return comparison;
                return StringComparer.Ordinal.Compare(
                    Convert.ToString(left["curve_type"], CultureInfo.InvariantCulture),
                    Convert.ToString(right["curve_type"], CultureInfo.InvariantCulture));
            });
            List<object> outputRecords = (List<object>)report["section_output_records"];
            for (int i = 0; i < sortedOutputRecords.Count; i++) outputRecords.Add(sortedOutputRecords[i]);
            report["section_output_domain_fingerprint"] = F12DomainFingerprint(sortedOutputRecords);
            report["section_output_count"] = outputRecords.Count;
            int sectionLoops = section.GetNumberOfLoops();
            report["section_loop_count"] = sectionLoops;
            int outputOpenComponents, outputClosedComponents;
            bool outputBranch;
            BuildEndpointGraph(outputTopologyCurves, EndpointToleranceMm / mmPerPartUnit,
                out outputOpenComponents, out outputClosedComponents, out outputBranch);
            report["section_output_open_component_count"] = outputOpenComponents;
            report["section_output_closed_component_count"] = outputClosedComponents;
            report["section_output_endpoint_branch_detected"] = outputBranch;
            report["section_loop_topology"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "section_reported_loop_count", sectionLoops },
                { "output_open_component_count", outputOpenComponents },
                { "output_closed_component_count", outputClosedComponents },
                { "output_component_total", outputOpenComponents + outputClosedComponents },
                { "closed_component_classification_is_independent_of_open_components", true }
            };

            List<Dictionary<string, object>> inputRecords = F12InputRecordDictionaries(report);
            Dictionary<string, object> domainMatch = MatchF12GeometryDomains(inputRecords, sortedOutputRecords,
                EndpointToleranceMm);
            domainMatch["input_domain_fingerprint"] = report["input_geometry_domain_fingerprint"];
            domainMatch["output_domain_fingerprint"] = report["section_output_domain_fingerprint"];
            report["section_domain_match"] = domainMatch;
            Dictionary<string, object> referenceExclusion = F12VerifyReferenceOutputExclusion(
                report, sortedOutputRecords, EndpointToleranceMm);
            report["reference_output_exclusion"] = referenceExclusion;

            int outerLoops = 0;
            int innerLoops = 0;
            int classifiedLoops = 0;
            bool classificationSucceeded = closedComponents == 0;
            string classificationNote = null;
            if (closedComponents > 0)
            {
                List<NXObject> closedCurves = new List<NXObject>();
                for (int i = 0; i < inputCurves.Length; i++)
                    if (inputCurves[i].InClosedGraphComponent) closedCurves.Add(inputCurves[i].Curve);
                classificationSucceeded = TryClassifySections(part, uf, closedCurves,
                    EndpointToleranceMm / mmPerPartUnit, closedComponents,
                    out outerLoops, out innerLoops, out classifiedLoops, out classificationNote);
            }
            if (classificationSucceeded && classifiedLoops != outputClosedComponents)
            {
                classificationSucceeded = false;
                classificationNote = "CLOSED_SOURCE_CLASSIFICATION_DISAGREES_WITH_OUTPUT_CLOSED_COMPONENTS";
            }
            bool sectionTopologyMatches = sectionLoops == outputOpenComponents + outputClosedComponents;
            if (!sectionTopologyMatches && classificationNote == null)
                classificationNote = "SECTION_LOOP_COUNT_DISAGREES_WITH_OUTPUT_COMPONENT_TOPOLOGY";
            classificationSucceeded = classificationSucceeded && sectionTopologyMatches;
            report["outer_loop_count"] = classificationSucceeded ? (object)outerLoops : null;
            report["inner_loop_count"] = classificationSucceeded ? (object)innerLoops : null;
            report["section_loop_classification_status"] = classificationSucceeded
                ? (closedComponents == 0 ? "VERIFIED_OPEN_ONLY_TOPOLOGY" : "VERIFIED_CLOSED_AND_OPEN_TOPOLOGY")
                : "UNAVAILABLE";
            report["section_loop_classification_failure_reason"] = classificationNote;

            Dictionary<string, object> sectionInput = (Dictionary<string, object>)report["section_input"];
            sectionInput["status"] = "CALLER_SECTION_READBACK";
            sectionInput["allow_ref_crvs"] = false;
            sectionInput["allow_ref_crvs_setting_status"] = "CALLER_MUST_SET_FALSE";
            sectionInput["allow_ref_crvs_readback"] = "NOT_READABLE_WITH_CURRENT_CONTRACT";
            sectionInput["caller_must_set_allow_ref_crvs_false"] = true;
            sectionInput["relation_readback_status"] = sectionInputRelation["status"];
            sectionInput["feature_rule_readback_status"] = sectionInputRelation["whole_sketch_feature_rule_status"];
            report["section_input"] = sectionInput;
            List<string> sectionFailures = new List<string>();
            if (!String.Equals(Convert.ToString(domainMatch["status"], CultureInfo.InvariantCulture), "MATCHED", StringComparison.Ordinal))
                sectionFailures.Add("SECTION_OUTPUT_GEOMETRY_DOMAIN_MISMATCH");
            string referenceStatus = Convert.ToString(referenceExclusion["status"], CultureInfo.InvariantCulture);
            if (!String.Equals(referenceStatus, "VERIFIED_NO_REFERENCE_GEOMETRY_IN_OUTPUT", StringComparison.Ordinal) &&
                !String.Equals(referenceStatus, "VERIFIED_UF_REFERENCE_STATUS_AND_SECTION_FILTER", StringComparison.Ordinal) &&
                !String.Equals(referenceStatus, "NO_REFERENCE_CURVES_PRESENT", StringComparison.Ordinal))
                sectionFailures.Add(referenceStatus);
            string relationStatus = Convert.ToString(sectionInputRelation["status"], CultureInfo.InvariantCulture);
            bool relationSupported = String.Equals(relationStatus, "MATCHED_SOURCE_CURVES", StringComparison.Ordinal) ||
                String.Equals(relationStatus, "WHOLE_SKETCH_FEATURE_RULE_SOURCE", StringComparison.Ordinal);
            if (!relationSupported)
                sectionFailures.Add("SECTION_INPUT_RELATION_NOT_VERIFIED_" + (String.IsNullOrEmpty(relationStatus) ? "MISSING" : relationStatus));
            string featureRuleStatus = Convert.ToString(sectionInputRelation["whole_sketch_feature_rule_status"],
                CultureInfo.InvariantCulture);
            if (!String.Equals(featureRuleStatus, "VERIFIED", StringComparison.Ordinal))
                sectionFailures.Add("SECTION_RULE_NOT_VERIFIED_" + (String.IsNullOrEmpty(featureRuleStatus) ? "MISSING" : featureRuleStatus));
            if (!classificationSucceeded) sectionFailures.Add(classificationNote ?? "SECTION_LOOP_CLASSIFICATION_FAILED");
            if (branch || outputBranch) sectionFailures.Add("ENDPOINT_GRAPH_BRANCH_DETECTED");
            if (sectionFailures.Count == 0)
            {
                report["status"] = "VERIFIED";
                report["failure_reason"] = null;
            }
            else
            {
                report["status"] = "SECTION_DOMAIN_MISMATCH";
                sectionFailures.Sort(StringComparer.Ordinal);
                report["failure_reason"] = String.Join(";", F12UniqueStrings(sectionFailures));
            }
        }
        catch (SectionCleanupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            report["status"] = "ANALYSIS_UNAVAILABLE";
            report["failure_reason"] = "F12_SECTION_READBACK_FAILED_" + ex.GetType().Name;
        }
        return report;
    }

    private static Dictionary<string, object> BuildF12InputDomain(Part part, Sketch sketch)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (sketch == null) throw new ArgumentNullException("sketch");

        Dictionary<string, object> report = NewF12Report();
        List<object> inputRecords = (List<object>)report["input_geometry_records"];
        List<object> excludedReferences = (List<object>)report["excluded_reference_records"];
        List<object> unsupportedActive = (List<object>)report["unsupported_active_curve_records"];
        List<CurveInfo> inputCurves = new List<CurveInfo>();
        List<Dictionary<string, object>> sortedInputRecords = new List<Dictionary<string, object>>();
        List<string> failureReasons = new List<string>();
        double mmPerPartUnit;

        if (!TryF12UnitScale(part, out mmPerPartUnit))
        {
            report["failure_reason"] = "UNSUPPORTED_PART_UNITS";
            return report;
        }
        report["units"] = "mm";
        UFSession uf;
        try
        {
            uf = UFSession.GetUFSession();
            NXObject[] allGeometry = sketch.GetAllGeometry();
            if (allGeometry == null)
                throw new InvalidOperationException("Sketch.GetAllGeometry returned null.");

            HashSet<string> seenJournalIds = new HashSet<string>(StringComparer.Ordinal);
            int sketchCurveCount = 0;
            for (int i = 0; i < allGeometry.Length; i++)
            {
                Curve curve = allGeometry[i] as Curve;
                if (curve == null) continue;
                sketchCurveCount++;
                string journalId = curve.JournalIdentifier;
                if (String.IsNullOrEmpty(journalId) || !seenJournalIds.Add(journalId))
                {
                    failureReasons.Add("SOURCE_JOURNAL_ID_MISSING_OR_DUPLICATE");
                    continue;
                }

                UFSket.ReferenceStatus referenceStatus;
                try
                {
                    uf.Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out referenceStatus);
                }
                catch (Exception ex)
                {
                    Dictionary<string, object> failedStatus = new Dictionary<string, object>(StringComparer.Ordinal);
                    failedStatus["source_jid"] = journalId;
                    failedStatus["curve_type"] = curve.GetType().Name;
                    failedStatus["status"] = "UNAVAILABLE";
                    failedStatus["reason"] = "UF_ASK_REFERENCE_STATUS_FAILED_" + ex.GetType().Name;
                    ((List<object>)report["reference_status_evidence"]).Add(failedStatus);
                    failureReasons.Add("REFERENCE_STATUS_UNAVAILABLE");
                    continue;
                }

                int referenceStatusValue = Convert.ToInt32(referenceStatus, CultureInfo.InvariantCulture);
                bool isActive = referenceStatus == UFSket.ReferenceStatus.Active;
                bool isReference = referenceStatus == UFSket.ReferenceStatus.Reference;
                Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
                evidence["source_jid"] = journalId;
                evidence["curve_type"] = curve.GetType().Name;
                evidence["uf_reference_status"] = referenceStatus.ToString();
                evidence["uf_reference_status_value"] = referenceStatusValue;
                evidence["classification"] = isReference ? "EXCLUDED_REFERENCE" :
                    isActive ? "ACTIVE_INPUT_CANDIDATE" : "UNKNOWN_STATUS";
                ((List<object>)report["reference_status_evidence"]).Add(evidence);

                if (isReference)
                {
                    if (curve is Line || curve is Arc || curve is Ellipse || curve is Spline)
                    {
                        try
                        {
                            double[] referenceLimits = F12AskLimits(uf, curve);
                            Dictionary<string, object> referenceGeometry = F12CurveGeometryRecord(
                                uf, curve, referenceLimits, mmPerPartUnit);
                            evidence["geometry"] = referenceGeometry;
                            evidence["geometry_fingerprint"] = F12GeometryFingerprint(referenceGeometry);
                            evidence["geometry_status"] = "RECORDED_FOR_OUTPUT_EXCLUSION_CHECK";
                        }
                        catch (Exception ex)
                        {
                            evidence["geometry_status"] = "UNAVAILABLE_" + ex.GetType().Name;
                        }
                    }
                    else evidence["geometry_status"] = "UNSUPPORTED_REFERENCE_TYPE_EXCLUDED_BY_UF_STATUS";
                    excludedReferences.Add(evidence);
                    continue;
                }
                if (!isActive)
                {
                    failureReasons.Add("REFERENCE_STATUS_UNCLASSIFIED");
                    continue;
                }

                if (!(curve is Line) && !(curve is Arc) && !(curve is Ellipse) && !(curve is Spline))
                {
                    Dictionary<string, object> unsupported = new Dictionary<string, object>(StringComparer.Ordinal);
                    unsupported["source_jid"] = journalId;
                    unsupported["curve_type"] = curve.GetType().Name;
                    unsupported["uf_reference_status"] = referenceStatus.ToString();
                    unsupported["uf_reference_status_value"] = referenceStatusValue;
                    unsupported["reason"] = "ACTIVE_CURVE_TYPE_NOT_SUPPORTED";
                    unsupportedActive.Add(unsupported);
                    failureReasons.Add("UNSUPPORTED_ACTIVE_CURVE_TYPE");
                    continue;
                }

                CurveInfo info = new CurveInfo();
                info.Curve = curve;
                info.Id = journalId;
                info.Supported = true;
                try
                {
                    double[] limits = F12AskLimits(uf, curve);
                    info.StartParameter = limits[0];
                    info.EndParameter = limits[1];
                    info.Start = F12EvaluatePoint(uf, curve, limits[0]);
                    info.End = F12EvaluatePoint(uf, curve, limits[1]);
                    info.Length = curve.GetLength();
                    info.Closed = Distance(info.Start, info.End) <= EndpointToleranceMm / mmPerPartUnit;
                    info.EndpointValid = IsFinite(info.Start.X) && IsFinite(info.Start.Y) && IsFinite(info.Start.Z) &&
                        IsFinite(info.End.X) && IsFinite(info.End.Y) && IsFinite(info.End.Z) && IsFinite(info.Length) &&
                        IsFinite(info.StartParameter) && IsFinite(info.EndParameter);
                    if (!info.EndpointValid || info.Length <= EndpointToleranceMm / mmPerPartUnit)
                    {
                        failureReasons.Add(!info.EndpointValid ? "INPUT_ENDPOINT_OR_LENGTH_UNAVAILABLE" : "DEGENERATE_ACTIVE_CURVE");
                        continue;
                    }

                    Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
                    record["source_jid"] = journalId;
                    record["curve_type"] = curve.GetType().Name;
                    record["uf_reference_status"] = referenceStatus.ToString();
                    record["uf_reference_status_value"] = referenceStatusValue;
                    record["geometry"] = F12CurveGeometryRecord(uf, curve, limits, mmPerPartUnit);
                    record["geometry_fingerprint"] = F12GeometryFingerprint((Dictionary<string, object>)record["geometry"]);
                    sortedInputRecords.Add(record);
                    inputCurves.Add(info);
                }
                catch (Exception ex)
                {
                    Dictionary<string, object> failedGeometry = new Dictionary<string, object>(StringComparer.Ordinal);
                    failedGeometry["source_jid"] = journalId;
                    failedGeometry["curve_type"] = curve.GetType().Name;
                    failedGeometry["reason"] = "GEOMETRY_DOMAIN_READ_FAILED_" + ex.GetType().Name;
                    ((List<object>)report["geometry_read_failures"]).Add(failedGeometry);
                    failureReasons.Add("INPUT_GEOMETRY_DOMAIN_UNAVAILABLE");
                }
            }

            report["source_curve_count"] = sketchCurveCount;
            report["excluded_reference_curve_count"] = excludedReferences.Count;
            sortedInputRecords.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
            {
                return StringComparer.Ordinal.Compare((string)left["source_jid"], (string)right["source_jid"]);
            });
            for (int i = 0; i < sortedInputRecords.Count; i++) inputRecords.Add(sortedInputRecords[i]);
            report["input_curve_count"] = inputRecords.Count;
            report["input_geometry_domain_fingerprint"] = F12RecordDomainFingerprint(sortedInputRecords);
            report["section_input"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "status", "NOT_BUILT" },
                { "rule_kind", "CurveFeatureRule" },
                { "source_feature_jid", sketch.Feature == null ? null : sketch.Feature.JournalIdentifier },
                { "allow_ref_crvs", false },
                { "input_geometry_domain_fingerprint", report["input_geometry_domain_fingerprint"] },
                { "source_geometry_jids", F12SourceJournalIds(sortedInputRecords) }
            };

            if (failureReasons.Count == 0)
            {
                bool selfIntersection;
                bool nativeCheckFailed;
                bool splineCheckFailed;
                List<string> nativeFailures;
                F12ValidateInputGeometry(uf, inputCurves, mmPerPartUnit, report,
                    out selfIntersection, out nativeCheckFailed, out splineCheckFailed, out nativeFailures);
                for (int i = 0; i < nativeFailures.Count; i++) failureReasons.Add(nativeFailures[i]);

                int openComponents;
                int closedComponents;
                bool branch;
                BuildEndpointGraph(inputCurves, EndpointToleranceMm / mmPerPartUnit,
                    out openComponents, out closedComponents, out branch);
                report["open_component_count"] = openComponents;
                report["endpoint_graph_closed_component_count"] = closedComponents;
                report["endpoint_graph_branch_detected"] = branch;
                report["topology_status"] = branch ? "BRANCH_DETECTED" : "ENDPOINT_GRAPH_VERIFIED";
                report["open_curve_source_jids"] = F12ComponentJournalIds(inputCurves, false);
                report["closed_curve_source_jids"] = F12ComponentJournalIds(inputCurves, true);
                if (branch) failureReasons.Add("ENDPOINT_GRAPH_BRANCH_DETECTED");
                report["valid_closed_component_count"] = selfIntersection || nativeCheckFailed ||
                    splineCheckFailed || branch ? (object)null : closedComponents;
                if (failureReasons.Count == 0)
                {
                    report["status"] = "SECTION_ANALYSIS_PENDING";
                    report["failure_reason"] = null;
                    report["section_domain_match"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "status", "NOT_EVALUATED_SECTION_PENDING" },
                        { "input_domain_fingerprint", report["input_geometry_domain_fingerprint"] },
                        { "output_domain_fingerprint", null },
                        { "matched_geometry_pairs", new List<object>() },
                        { "unmatched_input_jids", new List<object>() },
                        { "unmatched_output_records", new List<object>() }
                    };
                }
                else
                {
                    failureReasons.Sort(StringComparer.Ordinal);
                    report["failure_reason"] = String.Join(";", F12UniqueStrings(failureReasons));
                    if (selfIntersection) report["status"] = "INVALID_SELF_INTERSECTION";
                    else if (branch) report["status"] = "INVALID_TOPOLOGY";
                    else if (failureReasons.Contains("DEGENERATE_ACTIVE_CURVE")) report["status"] = "INVALID_DEGENERATE";
                    else if (nativeCheckFailed || splineCheckFailed) report["status"] = "ANALYSIS_UNAVAILABLE";
                    else report["status"] = "INPUT_DOMAIN_UNAVAILABLE";
                }
            }
            else
            {
                report["status"] = "INPUT_DOMAIN_UNAVAILABLE";
                failureReasons.Sort(StringComparer.Ordinal);
                report["failure_reason"] = String.Join(";", F12UniqueStrings(failureReasons));
                report["topology_status"] = "NOT_EVALUATED_INPUT_DOMAIN_INCOMPLETE";
            }
        }
        catch (Exception ex)
        {
            report["status"] = "ANALYSIS_UNAVAILABLE";
            report["failure_reason"] = "F12_INPUT_DOMAIN_ANALYSIS_FAILED_" + ex.GetType().Name;
        }

        return report;
    }

    private static Dictionary<string, object> AnalyzeF12TemporaryWholeSketchSection(Part part,
        Sketch sketch, Dictionary<string, object> inputReport)
    {
        double mmPerPartUnit;
        if (!TryF12UnitScale(part, out mmPerPartUnit)) return inputReport;
        List<Dictionary<string, object>> inputRecords = F12InputRecordDictionaries(inputReport);
        if (inputRecords.Count == 0)
        {
            inputReport["status"] = "EMPTY_NO_ACTIVE_CURVES";
            inputReport["failure_reason"] = "NO_ACTIVE_NONREFERENCE_CURVES";
            inputReport["outer_loop_count"] = 0;
            inputReport["inner_loop_count"] = 0;
            inputReport["section_loop_count"] = 0;
            inputReport["topology_status"] = "EMPTY_INPUT_DOMAIN";
            return inputReport;
        }
        if (sketch.Feature == null)
        {
            inputReport["status"] = "ANALYSIS_UNAVAILABLE";
            inputReport["failure_reason"] = "SKETCH_FEATURE_UNAVAILABLE_FOR_WHOLE_SKETCH_RULE";
            return inputReport;
        }

        UFSession uf;
        HashSet<Tag> objectsBefore;
        HashSet<Tag> sectionsBefore;
        bool dirtyBefore;
        try
        {
            uf = UFSession.GetUFSession();
            objectsBefore = SnapshotPartObjects(part, uf);
            sectionsBefore = SnapshotSectionTags(part);
            dirtyBefore = part.IsModified;
        }
        catch (Exception ex)
        {
            inputReport["status"] = "ANALYSIS_UNAVAILABLE";
            inputReport["failure_reason"] = "SECTION_INVENTORY_BEFORE_UNAVAILABLE_" + ex.GetType().Name;
            return inputReport;
        }

        Session session = Session.GetSession();
        Session.UndoMarkId mark = default(Session.UndoMarkId);
        bool markSet = false;
        bool undone = false;
        Exception operationFailure = null;
        Exception cleanupFailure = null;
        Section section = null;
        Dictionary<string, object> result = inputReport;
        try
        {
            mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F12 whole-sketch Section domain analysis");
            markSet = true;
            section = part.Sections.CreateSection(0.0095 / mmPerPartUnit, 0.01 / mmPerPartUnit, 0.5);
            section.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
            section.SetAllowRefCrvs(false);

            CurveInfo[] inputCurves;
            int openComponents, closedComponents;
            bool branch;
            string topologyFailure;
            if (!F12ReadInputTopology(sketch, uf, mmPerPartUnit,
                F12InputJournalIdSet(inputRecords), out inputCurves, out openComponents,
                out closedComponents, out branch, out topologyFailure))
                throw new InvalidOperationException(topologyFailure);
            if (inputCurves.Length == 0) throw new InvalidOperationException("F12 input geometry disappeared before Section construction.");

            CurveInfo seedInfo = inputCurves[0];
            for (int i = 1; i < inputCurves.Length; i++)
                if (StringComparer.Ordinal.Compare(inputCurves[i].Id, seedInfo.Id) < 0) seedInfo = inputCurves[i];
            CurveFeatureRule rule = part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sketch.Feature });
            section.AddToSection(new SelectionIntentRule[] { rule }, seedInfo.Curve, null, null,
                seedInfo.Start, Section.Mode.Create, false);

            result = AnalyzeF12SectionDomain(part, sketch, section);
            Dictionary<string, object> sectionInput = (Dictionary<string, object>)result["section_input"];
            sectionInput["status"] = "WHOLE_SKETCH_FEATURE_RULE_ADDED";
            sectionInput["rule_kind"] = "CurveFeatureRule";
            sectionInput["source_feature_jid"] = sketch.Feature.JournalIdentifier;
            sectionInput["allow_ref_crvs"] = false;
            sectionInput["allow_ref_crvs_setting_status"] = "SET_FALSE";
            sectionInput["seed_source_jid"] = seedInfo.Id;
            result["section_input"] = sectionInput;
            result["temporary_section_cleanup_status"] = "PENDING";
        }
        catch (Exception ex)
        {
            operationFailure = ex;
        }
        finally
        {
            if (markSet)
            {
                try
                {
                    Section[] currentSections = part.Sections.ToArray();
                    for (int i = 0; i < currentSections.Length; i++)
                    {
                        if (sectionsBefore.Contains(currentSections[i].Tag)) continue;
                        currentSections[i].Destroy();
                    }
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                try
                {
                    session.UndoToMark(mark, null);
                    undone = true;
                }
                catch (Exception ex)
                {
                    if (cleanupFailure == null) cleanupFailure = ex;
                }
                if (undone)
                {
                    try { session.DeleteUndoMark(mark, null); }
                    catch (Exception ex) { if (cleanupFailure == null) cleanupFailure = ex; }
                }
            }
        }

        if (!markSet)
        {
            result["status"] = "ANALYSIS_UNAVAILABLE";
            result["failure_reason"] = "SECTION_UNDO_MARK_UNAVAILABLE";
            return result;
        }
        if (cleanupFailure != null || !undone)
            throw new SectionCleanupException("F12 temporary Section cleanup/undo failed; model restoration is not verified.",
                cleanupFailure ?? operationFailure);
        try
        {
            HashSet<Tag> objectsAfter = SnapshotPartObjects(part, uf);
            bool dirtyAfter = part.IsModified;
            if (dirtyAfter != dirtyBefore || !SameTags(objectsBefore, objectsAfter))
                throw new SectionCleanupException("F12 Section analysis did not restore part inventory and modified state.", operationFailure);
        }
        catch (SectionCleanupException) { throw; }
        catch (Exception ex)
        {
            throw new SectionCleanupException("F12 Section cleanup could not verify restored part inventory.", ex);
        }

        if (operationFailure != null)
        {
            result["status"] = "ANALYSIS_UNAVAILABLE";
            result["failure_reason"] = "F12_TEMPORARY_SECTION_FAILED_" + operationFailure.GetType().Name;
        }
        else
        {
            result["temporary_section_cleanup_status"] = "VERIFIED_UNDONE_INVENTORY_AND_DIRTY_STATE";
        }
        return result;
    }

    private static bool F12ReadInputTopology(Sketch sketch, UFSession uf, double mmPerPartUnit,
        HashSet<string> expectedJids, out CurveInfo[] curves, out int openComponents,
        out int closedComponents, out bool branch, out string failureReason)
    {
        List<CurveInfo> result = new List<CurveInfo>();
        openComponents = 0;
        closedComponents = 0;
        branch = false;
        failureReason = null;
        try
        {
            NXObject[] allGeometry = sketch.GetAllGeometry();
            if (allGeometry == null) throw new InvalidOperationException("Sketch.GetAllGeometry returned null.");
            HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < allGeometry.Length; i++)
            {
                Curve curve = allGeometry[i] as Curve;
                if (curve == null) continue;
                UFSket.ReferenceStatus referenceStatus;
                uf.Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out referenceStatus);
                if (referenceStatus == UFSket.ReferenceStatus.Reference) continue;
                if (referenceStatus != UFSket.ReferenceStatus.Active)
                    throw new InvalidOperationException("UF sketch reference status was not Active or Reference.");
                string jid = curve.JournalIdentifier;
                if (String.IsNullOrEmpty(jid) || !expectedJids.Contains(jid) || !found.Add(jid))
                    throw new InvalidOperationException("Active input curve JIDs changed or were not unique during Section analysis.");
                if (!(curve is Line) && !(curve is Arc) && !(curve is Ellipse) && !(curve is Spline))
                    throw new InvalidOperationException("An active sketch curve has an unsupported F12 type.");
                double[] limits = F12AskLimits(uf, curve);
                CurveInfo info = new CurveInfo();
                info.Curve = curve;
                info.Id = jid;
                info.Supported = true;
                info.StartParameter = limits[0];
                info.EndParameter = limits[1];
                info.Start = F12EvaluatePoint(uf, curve, limits[0]);
                info.End = F12EvaluatePoint(uf, curve, limits[1]);
                info.Length = curve.GetLength();
                info.Closed = Distance(info.Start, info.End) <= EndpointToleranceMm / mmPerPartUnit;
                info.EndpointValid = IsFinite(info.Start.X) && IsFinite(info.Start.Y) && IsFinite(info.Start.Z) &&
                    IsFinite(info.End.X) && IsFinite(info.End.Y) && IsFinite(info.End.Z) && IsFinite(info.Length);
                if (!info.EndpointValid || info.Length <= EndpointToleranceMm / mmPerPartUnit)
                    throw new InvalidOperationException("Active input curve endpoint or length was invalid.");
                result.Add(info);
            }
            if (found.Count != expectedJids.Count)
                throw new InvalidOperationException("Active sketch curve set changed during Section analysis.");
            BuildEndpointGraph(result, EndpointToleranceMm / mmPerPartUnit,
                out openComponents, out closedComponents, out branch);
            curves = result.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            failureReason = "INPUT_TOPOLOGY_READ_FAILED_" + ex.GetType().Name;
            curves = new CurveInfo[0];
            return false;
        }
    }

    private static Dictionary<string, object> F12ReadSectionInputEvidence(Section section,
        Sketch sketch, UFSession uf, double mmPerPartUnit,
        List<Dictionary<string, object>> expectedInputRecords)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> recordObjects = new List<object>();
        List<Dictionary<string, object>> actualCurveRecords = new List<Dictionary<string, object>>();
        List<object> nonCurveInputs = new List<object>();
        List<object> sectionRuleRecords = new List<object>();
        evidence["section_input_records"] = recordObjects;
        evidence["non_curve_rule_sources"] = nonCurveInputs;
        evidence["section_rule_records"] = sectionRuleRecords;
        evidence["section_input_domain_fingerprint"] = null;
        evidence["section_data_count"] = 0;
        evidence["section_element_count"] = 0;
        evidence["whole_sketch_feature_source_observed"] = false;
        evidence["whole_sketch_feature_rule_observed"] = false;
        try
        {
            SectionData[] sections;
            section.GetSectionData(out sections);
            if (sections == null) throw new InvalidOperationException("Section.GetSectionData returned null.");
            int elementCount = 0;
            bool featureSourceObserved = false;
            bool featureRuleObserved = false;
            bool featureRuleMatchesSketch = false;
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                if (sections[sectionIndex] == null) throw new InvalidOperationException("Section.GetSectionData contains null SectionData.");
                SelectionIntentRule[] rules;
                sections[sectionIndex].GetRules(out rules);
                if (rules == null) throw new InvalidOperationException("SectionData.GetRules returned null.");
                for (int ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
                {
                    SelectionIntentRule rule = rules[ruleIndex];
                    if (rule == null) throw new InvalidOperationException("SectionData.GetRules contains null rule.");
                    Dictionary<string, object> ruleRecord = new Dictionary<string, object>(StringComparer.Ordinal);
                    ruleRecord["section_index"] = sectionIndex;
                    ruleRecord["rule_type"] = rule.Type.ToString();
                    CurveFeatureRule featureRule = rule as CurveFeatureRule;
                    if (featureRule != null)
                    {
                        featureRuleObserved = true;
                        Feature[] features;
                        featureRule.GetData(out features);
                        if (features == null) throw new InvalidOperationException("CurveFeatureRule.GetData returned null.");
                        List<object> featureJids = new List<object>();
                        bool containsSketchFeature = false;
                        for (int featureIndex = 0; featureIndex < features.Length; featureIndex++)
                        {
                            if (features[featureIndex] == null) throw new InvalidOperationException("CurveFeatureRule contains null Feature.");
                            string featureJid = features[featureIndex].JournalIdentifier;
                            featureJids.Add(featureJid);
                            if (sketch.Feature != null && features[featureIndex].Tag == sketch.Feature.Tag)
                                containsSketchFeature = true;
                        }
                        featureJids.Sort(delegate(object left, object right)
                        {
                            return StringComparer.Ordinal.Compare(
                                Convert.ToString(left, CultureInfo.InvariantCulture),
                                Convert.ToString(right, CultureInfo.InvariantCulture));
                        });
                        ruleRecord["source_feature_jids"] = featureJids;
                        ruleRecord["contains_sketch_feature"] = containsSketchFeature;
                        featureRuleMatchesSketch = featureRuleMatchesSketch ||
                            (containsSketchFeature && features.Length == 1);
                    }
                    sectionRuleRecords.Add(ruleRecord);
                }
                SectionElementData[] elements;
                sections[sectionIndex].GetSectionElementsData(out elements);
                if (elements == null) throw new InvalidOperationException("Section.GetSectionElementsData returned null.");
                for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
                {
                    SectionElementData elementData = elements[elementIndex];
                    if (elementData == null) throw new InvalidOperationException("Section element data contains null.");
                    DisplayableObject selected;
                    DisplayableObject startConnector;
                    DisplayableObject endConnector;
                    Point3d startPoint;
                    Point3d endPoint;
                    elementData.GetSectionElementData1(out selected, out startConnector,
                        out startPoint, out endConnector, out endPoint);
                    elementCount++;
                    NXObject selectedObject = selected as NXObject;
                    if (selectedObject == null)
                    {
                        Dictionary<string, object> missing = new Dictionary<string, object>(StringComparer.Ordinal);
                        missing["section_index"] = sectionIndex;
                        missing["element_index"] = elementIndex;
                        missing["status"] = "SELECTED_ENTITY_NOT_NXOBJECT";
                        nonCurveInputs.Add(missing);
                        continue;
                    }
                    string selectedJid = selectedObject.JournalIdentifier;
                    Curve selectedCurve = selectedObject as Curve;
                    if (selectedCurve == null)
                    {
                        Dictionary<string, object> sourceRecord = new Dictionary<string, object>(StringComparer.Ordinal);
                        sourceRecord["section_index"] = sectionIndex;
                        sourceRecord["element_index"] = elementIndex;
                        sourceRecord["source_jid"] = selectedJid;
                        sourceRecord["source_type"] = selectedObject.GetType().Name;
                        sourceRecord["start_point_mm"] = F12PointRecord(startPoint, mmPerPartUnit);
                        sourceRecord["end_point_mm"] = F12PointRecord(endPoint, mmPerPartUnit);
                        nonCurveInputs.Add(sourceRecord);
                        if ((sketch.Feature != null && selectedObject.Tag == sketch.Feature.Tag) ||
                            Object.ReferenceEquals(selectedObject, sketch)) featureSourceObserved = true;
                        continue;
                    }
                    if (!(selectedCurve is Line) && !(selectedCurve is Arc) &&
                        !(selectedCurve is Ellipse) && !(selectedCurve is Spline))
                        throw new InvalidOperationException("Section input contains an unsupported Curve type.");
                    double[] limits = F12AskLimits(uf, selectedCurve);
                    Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
                    record["source_jid"] = selectedJid;
                    record["curve_type"] = selectedCurve.GetType().Name;
                    record["section_index"] = sectionIndex;
                    record["element_index"] = elementIndex;
                    record["geometry"] = F12CurveGeometryRecord(uf, selectedCurve, limits, mmPerPartUnit);
                    record["geometry_fingerprint"] = F12GeometryFingerprint((Dictionary<string, object>)record["geometry"]);
                    record["start_connector_jid"] = startConnector == null ? null : startConnector.JournalIdentifier;
                    record["end_connector_jid"] = endConnector == null ? null : endConnector.JournalIdentifier;
                    actualCurveRecords.Add(record);
                }
            }
            evidence["section_data_count"] = sections.Length;
            evidence["section_element_count"] = elementCount;
            evidence["whole_sketch_feature_source_observed"] = featureSourceObserved;
            evidence["whole_sketch_feature_rule_observed"] = featureRuleObserved;
            evidence["whole_sketch_feature_rule_matches_sketch"] = featureRuleMatchesSketch;
            evidence["whole_sketch_feature_rule_status"] = featureRuleMatchesSketch ? "VERIFIED" :
                featureRuleObserved || sectionRuleRecords.Count > 0 ? "MISMATCH" : "UNAVAILABLE";
            actualCurveRecords.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
            {
                return StringComparer.Ordinal.Compare(F12String(left, "source_jid"), F12String(right, "source_jid"));
            });
            for (int i = 0; i < actualCurveRecords.Count; i++) recordObjects.Add(actualCurveRecords[i]);
            evidence["section_input_domain_fingerprint"] = F12DomainFingerprint(actualCurveRecords);

            HashSet<string> expectedJids = F12InputJournalIdSet(expectedInputRecords);
            HashSet<string> actualJids = F12InputJournalIdSet(actualCurveRecords);
            bool sameJids = expectedJids.SetEquals(actualJids) && expectedJids.Count == actualCurveRecords.Count;
            Dictionary<string, object> match = MatchF12GeometryDomains(expectedInputRecords,
                actualCurveRecords, EndpointToleranceMm);
            evidence["geometry_domain_match"] = match;
            evidence["source_jid_set_match"] = sameJids;
            if (sameJids && String.Equals(Convert.ToString(match["status"], CultureInfo.InvariantCulture),
                "MATCHED", StringComparison.Ordinal))
            {
                evidence["status"] = "MATCHED_SOURCE_CURVES";
                evidence["domain_basis"] = "SECTION_DATA_EXPANDED_TO_NATIVE_SOURCE_CURVES";
            }
            else if (actualCurveRecords.Count == 0 && (featureSourceObserved || featureRuleMatchesSketch))
            {
                evidence["status"] = "WHOLE_SKETCH_FEATURE_RULE_SOURCE";
                evidence["domain_basis"] = "SKETCH_FEATURE_RULE_PLUS_UF_FILTERED_SOURCE_DOMAIN";
                evidence["section_input_domain_fingerprint"] = F12DomainFingerprint(expectedInputRecords);
                evidence["resolved_source_geometry_jids"] = F12SourceJournalIds(expectedInputRecords);
            }
            else if (actualCurveRecords.Count == 0 && expectedInputRecords.Count == 0 && nonCurveInputs.Count == 0)
            {
                evidence["status"] = "MATCHED_EMPTY_SOURCE_DOMAIN";
                evidence["domain_basis"] = "EMPTY_UF_FILTERED_SKETCH_DOMAIN";
            }
            else
            {
                evidence["status"] = actualCurveRecords.Count > 0 ? "MISMATCH" : "INPUT_RELATION_UNAVAILABLE";
                evidence["reason"] = "SECTION_DATA_DID_NOT_RESOLVE_TO_EXPECTED_ACTIVE_CURVE_DOMAIN";
            }
        }
        catch (Exception ex)
        {
            evidence["status"] = "INPUT_RELATION_UNAVAILABLE";
            evidence["reason"] = "SECTION_DATA_READ_FAILED_" + ex.GetType().Name;
            evidence["whole_sketch_feature_rule_status"] = "UNAVAILABLE";
        }
        return evidence;
    }

    private static List<Dictionary<string, object>> F12InputRecordDictionaries(Dictionary<string, object> report)
    {
        List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
        IList records = report["input_geometry_records"] as IList;
        if (records == null) return result;
        for (int i = 0; i < records.Count; i++)
        {
            Dictionary<string, object> record = records[i] as Dictionary<string, object>;
            if (record != null) result.Add(record);
        }
        return result;
    }

    private static HashSet<string> F12InputJournalIdSet(List<Dictionary<string, object>> records)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < records.Count; i++)
        {
            string jid = F12String(records[i], "source_jid");
            if (!String.IsNullOrEmpty(jid)) result.Add(jid);
        }
        return result;
    }

    private static void F12ValidateInputGeometry(UFSession uf, List<CurveInfo> curves,
        double mmPerPartUnit, Dictionary<string, object> report, out bool selfIntersection,
        out bool nativeCheckFailed, out bool splineCheckFailed, out List<string> failures)
    {
        selfIntersection = false;
        nativeCheckFailed = false;
        splineCheckFailed = false;
        failures = new List<string>();
        List<object> pairs = (List<object>)report["intersection_pairs"];
        List<string> notes = new List<string>();
        for (int i = 0; i < curves.Count; i++)
        {
            try
            {
                uf.Eval.Initialize(curves[i].Curve.Tag, out curves[i].Evaluator);
                if (curves[i].Evaluator == IntPtr.Zero)
                    throw new InvalidOperationException("UF evaluator initialization returned a null handle.");
            }
            catch
            {
                nativeCheckFailed = true;
                failures.Add("NATIVE_EVALUATOR_INITIALIZATION_FAILED");
            }
        }

        try
        {
            for (int i = 0; i < curves.Count; i++)
            {
                Spline spline = curves[i].Curve as Spline;
                if (spline == null) continue;
                try
                {
                    bool intersectsItself;
                    uf.Curve.IsSplineSelfInt(spline.Tag, out intersectsItself);
                    if (intersectsItself)
                    {
                        selfIntersection = true;
                        notes.Add("native_spline_self_intersection_detected:" + curves[i].Id);
                    }
                }
                catch
                {
                    splineCheckFailed = true;
                    failures.Add("NATIVE_SPLINE_SELF_INTERSECTION_CHECK_FAILED");
                }
            }

            for (int i = 0; i < curves.Count; i++)
            {
                for (int j = i + 1; j < curves.Count; j++)
                {
                    CurveInfo first = curves[i];
                    CurveInfo second = curves[j];
                    Dictionary<string, object> pair = new Dictionary<string, object>(StringComparer.Ordinal);
                    pair["geometry1_id"] = first.Id;
                    pair["geometry2_id"] = second.Id;
                    List<object> points = new List<object>();
                    pair["points"] = points;
                    int count = -1;
                    bool pairHasInteriorIntersection = false;
                    try
                    {
                        double[] data;
                        uf.Modl.IntersectCurveToCurve(first.Curve.Tag, second.Curve.Tag, out count, out data);
                        if (count < 0 || (count > 0 && (data == null || data.Length < count * 5)))
                            throw new InvalidOperationException("Native intersection output was incomplete.");
                        for (int k = 0; k < count; k++)
                        {
                            int offset = k * 5;
                            double x = data[offset];
                            double y = data[offset + 1];
                            double z = data[offset + 2];
                            points.Add(new double[] { x * mmPerPartUnit, y * mmPerPartUnit,
                                z * mmPerPartUnit, data[offset + 3], data[offset + 4] });
                            Point3d intersection = new Point3d(x, y, z);
                            bool endpointOnFirst = IsOpenEndpoint(first, intersection,
                                EndpointToleranceMm / mmPerPartUnit);
                            bool endpointOnSecond = IsOpenEndpoint(second, intersection,
                                EndpointToleranceMm / mmPerPartUnit);
                            if (!endpointOnFirst || !endpointOnSecond)
                            {
                                pairHasInteriorIntersection = true;
                                selfIntersection = true;
                                notes.Add("native_curve_intersection_not_at_two_open_endpoints:" + first.Id + ":" + second.Id);
                            }
                        }
                    }
                    catch
                    {
                        nativeCheckFailed = true;
                        failures.Add("NATIVE_CURVE_INTERSECTION_CHECK_FAILED");
                    }
                    pair["count"] = count;
                    pairs.Add(pair);

                    if (first.Curve is Line && second.Curve is Line)
                    {
                        if (HasCollinearLineOverlap(first, second, EndpointToleranceMm / mmPerPartUnit))
                        {
                            selfIntersection = true;
                            notes.Add("collinear_line_interval_overlap_detected:" + first.Id + ":" + second.Id);
                        }
                    }
                    else if (!pairHasInteriorIntersection)
                    {
                        try
                        {
                            bool overlap = IsConic(first) && IsConic(second)
                                ? HasConicOverlap(first, second, EndpointToleranceMm / mmPerPartUnit)
                                : HasCoincidentOverlap(uf, first, second, EndpointToleranceMm / mmPerPartUnit);
                            if (overlap)
                            {
                                selfIntersection = true;
                                notes.Add((IsConic(first) && IsConic(second)
                                    ? "native_conic_interval_overlap_detected:"
                                    : "native_closest_point_overlap_detected:") + first.Id + ":" + second.Id);
                            }
                        }
                        catch
                        {
                            nativeCheckFailed = true;
                            failures.Add("NATIVE_COINCIDENT_OVERLAP_CHECK_FAILED");
                        }
                    }
                }
            }
        }
        finally
        {
            for (int i = 0; i < curves.Count; i++)
            {
                if (curves[i].Evaluator == IntPtr.Zero) continue;
                try { uf.Eval.Free(curves[i].Evaluator); }
                catch
                {
                    nativeCheckFailed = true;
                    failures.Add("NATIVE_EVALUATOR_FREE_FAILED");
                }
                curves[i].Evaluator = IntPtr.Zero;
            }
        }

        notes.Sort(StringComparer.Ordinal);
        report["geometry_validation_notes"] = notes;
        report["native_intersection_check"] = nativeCheckFailed ? "FAILED" : "VERIFIED";
        report["spline_self_intersection_check"] = curves.Exists(delegate(CurveInfo info) { return info.Curve is Spline; })
            ? (splineCheckFailed ? "FAILED" : "VERIFIED") : "NOT_APPLICABLE";
        report["self_intersection_detected"] = selfIntersection;
        report["native_geometry_check_failed"] = nativeCheckFailed;
        report["spline_check_failed"] = splineCheckFailed;
        if (selfIntersection) failures.Add("NATIVE_SELF_INTERSECTION_OR_OVERLAP_DETECTED");
        if (nativeCheckFailed) failures.Add("NATIVE_INTERSECTION_CHECK_UNAVAILABLE");
        if (splineCheckFailed) failures.Add("SPLINE_SELF_INTERSECTION_CHECK_UNAVAILABLE");
    }

    private static Dictionary<string, object> F12VerifyReferenceOutputExclusion(
        Dictionary<string, object> report, List<Dictionary<string, object>> outputRecords, double toleranceMm)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> excluded = report["excluded_reference_records"] as List<object>;
        List<object> matchingOutputs = new List<object>();
        List<object> unavailableReferences = new List<object>();
        List<object> ufExcludedWithoutGeometry = new List<object>();
        if (excluded == null || excluded.Count == 0)
        {
            evidence["status"] = "NO_REFERENCE_CURVES_PRESENT";
            evidence["matching_section_output_records"] = matchingOutputs;
            evidence["unavailable_reference_geometry_jids"] = unavailableReferences;
            evidence["uf_excluded_references_without_domain_record"] = ufExcludedWithoutGeometry;
            return evidence;
        }
        for (int i = 0; i < excluded.Count; i++)
        {
            Dictionary<string, object> reference = excluded[i] as Dictionary<string, object>;
            if (reference != null && !reference.ContainsKey("geometry") &&
                String.Equals(Convert.ToString(reference.ContainsKey("geometry_status")
                    ? reference["geometry_status"] : null, CultureInfo.InvariantCulture),
                    "UNSUPPORTED_REFERENCE_TYPE_EXCLUDED_BY_UF_STATUS", StringComparison.Ordinal))
            {
                ufExcludedWithoutGeometry.Add(reference.ContainsKey("source_jid") ? reference["source_jid"] : null);
                continue;
            }
            if (reference == null || !reference.ContainsKey("geometry") ||
                !(reference["geometry"] is Dictionary<string, object>))
            {
                unavailableReferences.Add(reference == null || !reference.ContainsKey("source_jid")
                    ? null : reference["source_jid"]);
                continue;
            }
            for (int j = 0; j < outputRecords.Count; j++)
            {
                Dictionary<string, object> ignored;
                if (!F12GeometryRecordsEquivalent(reference, outputRecords[j], toleranceMm, out ignored)) continue;
                Dictionary<string, object> match = new Dictionary<string, object>(StringComparer.Ordinal);
                match["reference_source_jid"] = reference["source_jid"];
                match["section_output_jid"] = outputRecords[j].ContainsKey("section_output_jid")
                    ? outputRecords[j]["section_output_jid"] : null;
                match["output_record_index"] = j;
                matchingOutputs.Add(match);
            }
        }
        evidence["matching_section_output_records"] = matchingOutputs;
        evidence["unavailable_reference_geometry_jids"] = unavailableReferences;
        evidence["uf_excluded_references_without_domain_record"] = ufExcludedWithoutGeometry;
        evidence["status"] = unavailableReferences.Count > 0 ? "REFERENCE_GEOMETRY_UNAVAILABLE" :
            matchingOutputs.Count > 0 ? "REFERENCE_GEOMETRY_MATCHES_SECTION_OUTPUT" :
            ufExcludedWithoutGeometry.Count > 0 ? "VERIFIED_UF_REFERENCE_STATUS_AND_SECTION_FILTER" :
            "VERIFIED_NO_REFERENCE_GEOMETRY_IN_OUTPUT";
        return evidence;
    }

    private static Dictionary<string, object> NewF12Report()
    {
        Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.Ordinal);
        report["contract"] = "F12_WHOLE_SKETCH_SECTION_V1";
        report["status"] = "ANALYSIS_UNAVAILABLE";
        report["failure_reason"] = null;
        report["units"] = null;
        report["source_curve_count"] = 0;
        report["input_curve_count"] = 0;
        report["excluded_reference_curve_count"] = 0;
        report["native_intersection_check"] = "NOT_REQUIRED";
        report["spline_self_intersection_check"] = "NOT_APPLICABLE";
        report["self_intersection_detected"] = false;
        report["native_geometry_check_failed"] = false;
        report["spline_check_failed"] = false;
        report["intersection_pairs"] = new List<object>();
        report["geometry_validation_notes"] = new List<string>();
        report["input_geometry_domain_fingerprint"] = null;
        report["input_geometry_records"] = new List<object>();
        report["reference_status_evidence"] = new List<object>();
        report["excluded_reference_records"] = new List<object>();
        report["unsupported_active_curve_records"] = new List<object>();
        report["geometry_read_failures"] = new List<object>();
        report["open_component_count"] = 0;
        report["valid_closed_component_count"] = 0;
        report["endpoint_graph_closed_component_count"] = 0;
        report["endpoint_graph_branch_detected"] = false;
        report["topology_status"] = "NOT_EVALUATED";
        report["open_curve_source_jids"] = new List<object>();
        report["closed_curve_source_jids"] = new List<object>();
        report["outer_loop_count"] = null;
        report["inner_loop_count"] = null;
        report["section_loop_count"] = null;
        report["section_output_count"] = 0;
        report["section_input_records"] = new List<object>();
        report["section_input_domain_fingerprint"] = null;
        report["section_input_relation"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", "NOT_EVALUATED" },
            { "section_input_records", new List<object>() },
            { "section_input_domain_fingerprint", null }
        };
        report["section_output_open_component_count"] = 0;
        report["section_output_closed_component_count"] = 0;
        report["section_output_endpoint_branch_detected"] = false;
        report["section_loop_topology"] = null;
        report["section_loop_classification_status"] = "NOT_EVALUATED";
        report["section_loop_classification_failure_reason"] = null;
        report["reference_output_exclusion"] = null;
        report["section_input"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", "NOT_BUILT" },
            { "rule_kind", "CurveFeatureRule" },
            { "source_feature_jid", null },
            { "allow_ref_crvs", false },
            { "input_geometry_domain_fingerprint", null },
            { "source_geometry_jids", new List<object>() }
        };
        report["section_output_records"] = new List<object>();
        report["section_output_domain_fingerprint"] = null;
        report["section_domain_match"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", "NOT_EVALUATED" },
            { "input_domain_fingerprint", null },
            { "output_domain_fingerprint", null },
            { "matched_geometry_pairs", new List<object>() },
            { "unmatched_input_jids", new List<object>() },
            { "unmatched_output_records", new List<object>() }
        };
        return report;
    }

    private static bool TryF12UnitScale(Part part, out double mmPerPartUnit)
    {
        mmPerPartUnit = 1.0;
        if (part.PartUnits == BasePart.Units.Millimeters) return true;
        if (part.PartUnits == BasePart.Units.Inches)
        {
            mmPerPartUnit = 25.4;
            return true;
        }
        return false;
    }

    private static double[] F12AskLimits(UFSession uf, Curve curve)
    {
        IntPtr evaluator = IntPtr.Zero;
        try
        {
            uf.Eval.Initialize(curve.Tag, out evaluator);
            if (evaluator == IntPtr.Zero) throw new InvalidOperationException("UF evaluator initialization returned a null handle.");
            double[] limits = new double[2];
            uf.Eval.AskLimits(evaluator, limits);
            if (!IsFinite(limits[0]) || !IsFinite(limits[1]) || limits[0] == limits[1])
                throw new InvalidOperationException("Native curve parameter limits were invalid.");
            return limits;
        }
        finally
        {
            if (evaluator != IntPtr.Zero) uf.Eval.Free(evaluator);
        }
    }

    private static Point3d F12EvaluatePoint(UFSession uf, Curve curve, double parameter)
    {
        double[] result = new double[3];
        uf.Curve.EvaluateCurve(curve.Tag, parameter, 0, result);
        return new Point3d(result[0], result[1], result[2]);
    }

    private static Dictionary<string, object> F12CurveGeometryRecord(UFSession uf, Curve curve,
        double[] limits, double mmPerPartUnit)
    {
        Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
        Line line = curve as Line;
        Arc arc = curve as Arc;
        Ellipse ellipse = curve as Ellipse;
        Spline spline = curve as Spline;
        if (line != null) geometry["kind"] = "LINE";
        else if (arc != null) geometry["kind"] = "ARC";
        else if (ellipse != null) geometry["kind"] = "ELLIPSE";
        else if (spline != null) geometry["kind"] = "SPLINE";
        else throw new InvalidOperationException("F12 input curve type was not supported.");

        geometry["parameter_start"] = limits[0];
        geometry["parameter_end"] = limits[1];
        List<object> samples = new List<object>();
        for (int i = 0; i <= 4; i++)
        {
            double parameter = limits[0] + (limits[1] - limits[0]) * i / 4.0;
            Point3d point = F12EvaluatePoint(uf, curve, parameter);
            samples.Add(F12PointRecord(point, mmPerPartUnit));
        }
        geometry["domain_samples"] = samples;
        geometry["is_closed_domain"] = Distance(F12EvaluatePoint(uf, curve, limits[0]),
            F12EvaluatePoint(uf, curve, limits[1])) <= EndpointToleranceMm / mmPerPartUnit;

        if (line != null)
        {
            geometry["start"] = F12PointRecord(F12EvaluatePoint(uf, curve, limits[0]), mmPerPartUnit);
            geometry["end"] = F12PointRecord(F12EvaluatePoint(uf, curve, limits[1]), mmPerPartUnit);
            geometry["length_mm"] = curve.GetLength() * mmPerPartUnit;
        }
        else if (arc != null)
        {
            Point3d center;
            Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            Vector3d normal = Cross(xAxis, yAxis);
            geometry["center"] = F12PointRecord(center, mmPerPartUnit);
            geometry["radius_mm"] = arc.Radius * mmPerPartUnit;
            geometry["axis_x"] = F12VectorRecord(xAxis);
            geometry["axis_y"] = F12VectorRecord(yAxis);
            geometry["normal"] = F12VectorRecord(normal);
            geometry["start_angle_rad"] = arc.StartAngle;
            geometry["end_angle_rad"] = arc.EndAngle;
            geometry["length_mm"] = curve.GetLength() * mmPerPartUnit;
        }
        else if (ellipse != null)
        {
            double major, minor, polarStart, polarEnd, rotation;
            Point3d center;
            ellipse.GetParameters(out major, out minor, out polarStart, out polarEnd, out rotation, out center);
            Point3d frameCenter;
            Vector3d frameX, frameY;
            ellipse.GetOrientation(out frameCenter, out frameX, out frameY);
            Vector3d majorAxis = Add(Scale(frameX, Math.Cos(rotation)), Scale(frameY, Math.Sin(rotation)));
            Vector3d minorAxis = Add(Scale(frameX, -Math.Sin(rotation)), Scale(frameY, Math.Cos(rotation)));
            geometry["center"] = F12PointRecord(center, mmPerPartUnit);
            geometry["major_radius_mm"] = major * mmPerPartUnit;
            geometry["minor_radius_mm"] = minor * mmPerPartUnit;
            geometry["major_axis"] = F12VectorRecord(majorAxis);
            geometry["minor_axis"] = F12VectorRecord(minorAxis);
            geometry["normal"] = F12VectorRecord(Cross(frameX, frameY));
            geometry["rotation_rad"] = rotation;
            geometry["native_polar_start_rad"] = polarStart;
            geometry["native_polar_end_rad"] = polarEnd;
            geometry["length_mm"] = curve.GetLength() * mmPerPartUnit;
        }
        else
        {
            UFCurve.Spline native;
            uf.Curve.AskSplineData(spline.Tag, out native);
            if (native.poles == null || native.knots == null || native.num_poles <= 0 || native.order <= 0 ||
                native.poles.GetLength(0) != native.num_poles)
                throw new InvalidOperationException("Native spline definition was incomplete.");
            bool rational = native.is_rational != 0;
            if (rational && native.poles.GetLength(1) != 4)
                throw new InvalidOperationException("Rational native spline poles did not contain homogeneous XYZ and weight.");
            List<object> poles = new List<object>();
            List<object> cartesianControlPoints = new List<object>();
            for (int poleIndex = 0; poleIndex < native.poles.GetLength(0); poleIndex++)
            {
                List<object> pole = new List<object>();
                for (int coordinate = 0; coordinate < native.poles.GetLength(1); coordinate++)
                {
                    double value = native.poles[poleIndex, coordinate];
                    if (coordinate < 3) value *= mmPerPartUnit;
                    if (!IsFinite(value)) throw new InvalidOperationException("Native spline definition contained a non-finite pole.");
                    pole.Add(value);
                }
                poles.Add(pole);
                IList poleValues = pole;
                double[] cartesianPole;
                if (!F12TryCartesianPole(poleValues, rational, out cartesianPole))
                    throw new InvalidOperationException("Native spline pole could not be recovered to Cartesian millimetres with positive weight.");
                cartesianControlPoints.Add(cartesianPole);
            }
            List<object> knots = new List<object>();
            for (int i = 0; i < native.knots.Length; i++)
            {
                if (!IsFinite(native.knots[i])) throw new InvalidOperationException("Native spline definition contained a non-finite knot.");
                knots.Add(native.knots[i]);
            }
            Dictionary<string, object> nurbs = new Dictionary<string, object>(StringComparer.Ordinal);
            nurbs["degree"] = native.order - 1;
            nurbs["order"] = native.order;
            nurbs["pole_count"] = native.num_poles;
            nurbs["rational"] = native.is_rational != 0;
            nurbs["pole_coordinate_encoding"] = rational ? "HOMOGENEOUS_XYZ_W" : "CARTESIAN_XYZ";
            nurbs["knots"] = knots;
            nurbs["poles"] = poles;
            nurbs["cartesian_control_points_mm"] = cartesianControlPoints;
            nurbs["start_parameter"] = native.start_param;
            nurbs["end_parameter"] = native.end_param;
            geometry["nurbs_definition"] = nurbs;
            geometry["length_mm"] = curve.GetLength() * mmPerPartUnit;
        }

        return geometry;
    }

    private static double[] F12PointRecord(Point3d point, double mmPerPartUnit)
    {
        return new double[] { point.X * mmPerPartUnit, point.Y * mmPerPartUnit, point.Z * mmPerPartUnit };
    }

    private static double[] F12VectorRecord(Vector3d vector)
    {
        return new double[] { vector.X, vector.Y, vector.Z };
    }

    private static string F12GeometryFingerprint(Dictionary<string, object> geometry)
    {
        StringBuilder builder = new StringBuilder();
        string kind = F12String(geometry, "kind");
        Dictionary<string, object> canonical = new Dictionary<string, object>(StringComparer.Ordinal);
        canonical["kind"] = kind;
        if (kind == "LINE")
        {
            double[] start = F12Vector(geometry, "start");
            double[] end = F12Vector(geometry, "end");
            if (StringComparer.Ordinal.Compare(F12CanonicalText(start), F12CanonicalText(end)) <= 0)
            {
                canonical["endpoint_a"] = start;
                canonical["endpoint_b"] = end;
            }
            else
            {
                canonical["endpoint_a"] = end;
                canonical["endpoint_b"] = start;
            }
        }
        else if (kind == "ARC")
        {
            canonical["center"] = geometry["center"];
            canonical["radius_mm"] = geometry["radius_mm"];
            canonical["normal"] = F12CanonicalDirection(F12Vector(geometry, "normal"));
            bool closed = F12Boolean(geometry, "is_closed_domain");
            canonical["is_closed_domain"] = closed;
            if (!closed) canonical["domain_samples"] = F12CanonicalSampleSequence(geometry);
        }
        else if (kind == "ELLIPSE")
        {
            canonical["center"] = geometry["center"];
            canonical["major_radius_mm"] = geometry["major_radius_mm"];
            canonical["minor_radius_mm"] = geometry["minor_radius_mm"];
            canonical["normal"] = F12CanonicalDirection(F12Vector(geometry, "normal"));
            double major = F12Number(geometry, "major_radius_mm");
            double minor = F12Number(geometry, "minor_radius_mm");
            if (Math.Abs(major - minor) > EndpointToleranceMm)
                canonical["major_axis"] = F12CanonicalDirection(F12Vector(geometry, "major_axis"));
            bool closed = F12Boolean(geometry, "is_closed_domain");
            canonical["is_closed_domain"] = closed;
            if (!closed) canonical["domain_samples"] = F12CanonicalSampleSequence(geometry);
        }
        else if (kind == "SPLINE")
        {
            canonical["nurbs_definition_fingerprint"] = F12CanonicalNurbsFingerprint(geometry);
        }
        else
        {
            F12AppendCanonical(builder, geometry);
            return builder.ToString();
        }
        F12AppendCanonical(builder, canonical);
        return builder.ToString();
    }

    private static string F12CanonicalNurbsFingerprint(Dictionary<string, object> geometry)
    {
        Dictionary<string, object> nurbs = geometry.ContainsKey("nurbs_definition")
            ? geometry["nurbs_definition"] as Dictionary<string, object> : null;
        if (nurbs == null) return "missing-nurbs-definition";
        Dictionary<string, object> forwardRecord;
        Dictionary<string, object> reverseRecord;
        if (!F12BuildCanonicalNurbsRecord(nurbs, false, out forwardRecord) ||
            !F12BuildCanonicalNurbsRecord(nurbs, true, out reverseRecord))
            return "invalid-nurbs-definition";
        string forward = F12CanonicalText(forwardRecord);
        string reverse = F12CanonicalText(reverseRecord);
        return StringComparer.Ordinal.Compare(forward, reverse) <= 0 ? forward : reverse;
    }

    private static bool F12BuildCanonicalNurbsRecord(Dictionary<string, object> source,
        bool reversed, out Dictionary<string, object> result)
    {
        result = null;
        try
        {
            IList sourcePoles = source.ContainsKey("poles") ? source["poles"] as IList : null;
            IList sourceKnots = source.ContainsKey("knots") ? source["knots"] as IList : null;
            int order = F12Integer(source, "order");
            int poleCount = F12Integer(source, "pole_count");
            bool rational = F12Boolean(source, "rational");
            double[] normalizedKnots;
            double normalizedStart, normalizedEnd;
            double[] normalizedWeights;
            if (sourcePoles == null || sourceKnots == null || poleCount <= 0 || order <= 0 ||
                sourcePoles.Count != poleCount || sourceKnots.Count != poleCount + order ||
                !F12TryNormalizeKnots(sourceKnots, out normalizedKnots) ||
                !F12TryNormalizeSplineInterval(source, sourceKnots, out normalizedStart, out normalizedEnd) ||
                !F12TryGetNormalizedSplineWeights(sourcePoles, rational, out normalizedWeights)) return false;

            result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["degree"] = order - 1;
            result["order"] = order;
            result["pole_count"] = poleCount;
            result["rational"] = rational;
            List<object> cartesianPoles = new List<object>();
            for (int i = 0; i < poleCount; i++)
            {
                int sourceIndex = reversed ? poleCount - 1 - i : i;
                IList sourcePole = sourcePoles[sourceIndex] as IList;
                double[] cartesianPole;
                if (!F12TryCartesianPole(sourcePole, rational, out cartesianPole)) return false;
                cartesianPoles.Add(new double[] {
                    cartesianPole[0], cartesianPole[1], cartesianPole[2] });
            }
            result["cartesian_poles_mm"] = cartesianPoles;
            if (rational)
            {
                List<object> weights = new List<object>();
                for (int i = 0; i < poleCount; i++)
                    weights.Add(normalizedWeights[reversed ? poleCount - 1 - i : i]);
                result["normalized_weights"] = weights;
            }

            List<object> knots = new List<object>();
            for (int i = 0; i < normalizedKnots.Length; i++)
            {
                double value = normalizedKnots[reversed ? normalizedKnots.Length - 1 - i : i];
                if (reversed) value = 1.0 - value;
                knots.Add(F12StableUnitParameter(value));
            }
            result["normalized_knots"] = knots;
            double activeStart = reversed ? 1.0 - normalizedEnd : normalizedStart;
            double activeEnd = reversed ? 1.0 - normalizedStart : normalizedEnd;
            result["active_start"] = F12StableUnitParameter(activeStart);
            result["active_end"] = F12StableUnitParameter(activeEnd);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    private static double F12StableUnitParameter(double value)
    {
        // NX reverse knot transforms can differ by a few binary ULPs (0.3 vs
        // 1 - 0.7). Fingerprints use the same 1e-12 parameter tolerance as the
        // matcher, while geometry coordinates and rational weights stay unrounded.
        double rounded = Math.Round(value, 12, MidpointRounding.AwayFromZero);
        return rounded == 0.0 ? 0.0 : rounded;
    }

    private static bool F12TryNormalizeKnots(IList knots, out double[] normalized)
    {
        normalized = null;
        if (knots == null || knots.Count < 2) return false;
        double low = Convert.ToDouble(knots[0], CultureInfo.InvariantCulture);
        double high = Convert.ToDouble(knots[knots.Count - 1], CultureInfo.InvariantCulture);
        double range = high - low;
        if (!IsFinite(low) || !IsFinite(high) || !IsFinite(range) || range <= 0.0) return false;
        normalized = new double[knots.Count];
        double previous = Double.NegativeInfinity;
        for (int i = 0; i < knots.Count; i++)
        {
            double knot = Convert.ToDouble(knots[i], CultureInfo.InvariantCulture);
            if (!IsFinite(knot) || knot < low || knot > high || knot < previous) return false;
            double value = (knot - low) / range;
            if (!IsFinite(value)) return false;
            normalized[i] = value;
            previous = knot;
        }
        normalized[0] = 0.0;
        normalized[normalized.Length - 1] = 1.0;
        return true;
    }

    private static bool F12TryNormalizeSplineInterval(Dictionary<string, object> definition,
        IList knots, out double start, out double end)
    {
        start = Double.NaN;
        end = Double.NaN;
        if (knots == null || knots.Count < 2) return false;
        double low = Convert.ToDouble(knots[0], CultureInfo.InvariantCulture);
        double high = Convert.ToDouble(knots[knots.Count - 1], CultureInfo.InvariantCulture);
        double range = high - low;
        double nativeStart = F12Number(definition, "start_parameter");
        double nativeEnd = F12Number(definition, "end_parameter");
        if (!IsFinite(range) || range <= 0.0 || !IsFinite(nativeStart) || !IsFinite(nativeEnd)) return false;
        start = (nativeStart - low) / range;
        end = (nativeEnd - low) / range;
        return IsFinite(start) && IsFinite(end) && start >= -1.0e-12 && end <= 1.0 + 1.0e-12 && start < end;
    }

    private static bool F12TryGetNormalizedSplineWeights(IList poles, bool rational, out double[] weights)
    {
        // Rational UF poles are homogeneous XYZ and W. Normalize weights by one
        // common positive scale before relative comparison; zero/negative weights
        // cannot be safely dehomogenized by this matcher.
        weights = new double[0];
        if (!rational) return true;
        if (poles == null || poles.Count == 0) return false;
        double maximum = 0.0;
        double[] raw = new double[poles.Count];
        for (int i = 0; i < poles.Count; i++)
        {
            IList pole = poles[i] as IList;
            if (pole == null || pole.Count != 4) return false;
            double weight = Convert.ToDouble(pole[3], CultureInfo.InvariantCulture);
            if (!IsFinite(weight) || weight <= 0.0) return false;
            raw[i] = weight;
            maximum = Math.Max(maximum, weight);
        }
        if (!IsFinite(maximum) || maximum <= 0.0) return false;
        weights = new double[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            weights[i] = raw[i] / maximum;
            if (!IsFinite(weights[i]) || weights[i] <= 0.0) return false;
        }
        return true;
    }

    private static bool F12TryCartesianPole(IList pole, bool rational, out double[] cartesianMm)
    {
        cartesianMm = null;
        if (pole == null || (rational ? pole.Count != 4 : pole.Count < 3)) return false;
        double weight = 1.0;
        if (rational)
        {
            weight = Convert.ToDouble(pole[3], CultureInfo.InvariantCulture);
            if (!IsFinite(weight) || weight <= 0.0) return false;
        }
        cartesianMm = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double homogeneous = Convert.ToDouble(pole[i], CultureInfo.InvariantCulture);
            if (!IsFinite(homogeneous)) return false;
            cartesianMm[i] = homogeneous / weight;
            if (!IsFinite(cartesianMm[i])) return false;
        }
        return true;
    }

    private static List<object> F12CanonicalSampleSequence(Dictionary<string, object> geometry)
    {
        IList source = geometry.ContainsKey("domain_samples") ? geometry["domain_samples"] as IList : null;
        List<object> forward = new List<object>();
        List<object> reverse = new List<object>();
        if (source == null) return forward;
        for (int i = 0; i < source.Count; i++) forward.Add(source[i]);
        for (int i = source.Count - 1; i >= 0; i--) reverse.Add(source[i]);
        return StringComparer.Ordinal.Compare(F12CanonicalText(forward), F12CanonicalText(reverse)) <= 0
            ? forward : reverse;
    }

    private static double[] F12CanonicalDirection(double[] vector)
    {
        if (vector == null || vector.Length != 3) return vector;
        double magnitude = Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);
        if (magnitude <= 0.0) return vector;
        double[] result = new double[] { vector[0] / magnitude, vector[1] / magnitude, vector[2] / magnitude };
        int firstNonzero = 0;
        while (firstNonzero < result.Length && Math.Abs(result[firstNonzero]) <= 1.0e-15) firstNonzero++;
        if (firstNonzero < result.Length && result[firstNonzero] < 0.0)
            for (int i = 0; i < result.Length; i++) result[i] = -result[i];
        return result;
    }

    private static string F12CanonicalText(object value)
    {
        StringBuilder builder = new StringBuilder();
        F12AppendCanonical(builder, value);
        return builder.ToString();
    }
    private static string F12RecordDomainFingerprint(List<Dictionary<string, object>> records)
    {
        return F12DomainFingerprint(records);
    }

    /// <summary>
    /// Produces an order- and identity-independent fingerprint of a geometric domain.
    /// Records may be source or Section output records; only their complete geometry
    /// definitions participate in the fingerprint.
    /// </summary>
    public static string F12DomainFingerprint(IEnumerable<Dictionary<string, object>> records)
    {
        if (records == null) throw new ArgumentNullException("records");
        List<string> fingerprints = new List<string>();
        foreach (Dictionary<string, object> record in records)
        {
            if (record == null) throw new ArgumentException("Geometry-domain records cannot contain null.", "records");
            object fingerprintValue;
            string fingerprint;
            if (record.TryGetValue("geometry_fingerprint", out fingerprintValue))
                fingerprint = Convert.ToString(fingerprintValue, CultureInfo.InvariantCulture);
            else
            {
                Dictionary<string, object> geometry = record.ContainsKey("geometry")
                    ? record["geometry"] as Dictionary<string, object> : record;
                if (geometry == null) throw new ArgumentException("Geometry-domain record has no geometry definition.", "records");
                fingerprint = F12GeometryFingerprint(geometry);
            }
            fingerprints.Add(fingerprint ?? String.Empty);
        }
        fingerprints.Sort(StringComparer.Ordinal);
        StringBuilder builder = new StringBuilder("f12-geometry-domain-v1|");
        builder.Append(fingerprints.Count.ToString(CultureInfo.InvariantCulture)).Append('|');
        for (int i = 0; i < fingerprints.Count; i++)
            builder.Append(fingerprints[i].Length.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(fingerprints[i]).Append('|');
        return builder.ToString();
    }

    private static void F12AppendCanonical(StringBuilder builder, object value)
    {
        if (value == null)
        {
            builder.Append("null;");
            return;
        }
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            List<string> keys = new List<string>(dictionary.Keys);
            keys.Sort(StringComparer.Ordinal);
            builder.Append('{');
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                builder.Append(key.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(key).Append('=');
                F12AppendCanonical(builder, dictionary[key]);
            }
            builder.Append("};");
            return;
        }
        System.Collections.IList list = value as System.Collections.IList;
        if (list != null)
        {
            builder.Append('[');
            for (int i = 0; i < list.Count; i++) F12AppendCanonical(builder, list[i]);
            builder.Append("];");
            return;
        }
        if (value is string)
        {
            string text = (string)value;
            builder.Append('s').Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
            return;
        }
        if (value is bool)
        {
            builder.Append((bool)value ? "true;" : "false;");
            return;
        }
        if (value is double)
        {
            builder.Append(((double)value).ToString("R", CultureInfo.InvariantCulture)).Append(';');
            return;
        }
        if (value is float)
        {
            builder.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)).Append(';');
            return;
        }
        builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';');
    }

    private static List<object> F12SourceJournalIds(List<Dictionary<string, object>> records)
    {
        List<object> result = new List<object>();
        for (int i = 0; i < records.Count; i++) result.Add(records[i]["source_jid"]);
        return result;
    }

    private static List<string> F12UniqueStrings(List<string> values)
    {
        List<string> result = new List<string>();
        for (int i = 0; i < values.Count; i++)
            if (!result.Contains(values[i])) result.Add(values[i]);
        return result;
    }

    private static List<object> F12ComponentJournalIds(List<CurveInfo> curves, bool closed)
    {
        List<string> ids = new List<string>();
        for (int i = 0; i < curves.Count; i++)
            if (curves[i].InClosedGraphComponent == closed) ids.Add(curves[i].Id);
        ids.Sort(StringComparer.Ordinal);
        List<object> result = new List<object>();
        for (int i = 0; i < ids.Count; i++) result.Add(ids[i]);
        return result;
    }

    /// <summary>
    /// Pure one-to-one geometry-domain matcher for F12 source/output records. It ignores
    /// output Tags and Journal Identifiers and checks complete native definitions where
    /// the supported representation permits an exact comparison.
    /// </summary>
    internal static Dictionary<string, object> MatchF12GeometryDomains(
        List<Dictionary<string, object>> expectedRecords,
        List<Dictionary<string, object>> actualRecords,
        double toleranceMm)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> pairs = new List<object>();
        List<object> unmatchedExpected = new List<object>();
        List<object> unmatchedActual = new List<object>();
        bool[] consumed = new bool[actualRecords == null ? 0 : actualRecords.Count];
        if (expectedRecords == null || actualRecords == null || !IsFinite(toleranceMm) || toleranceMm < 0.0)
        {
            result["status"] = "MATCH_INPUT_INVALID";
            result["matched_geometry_pairs"] = pairs;
            result["unmatched_expected_records"] = unmatchedExpected;
            result["unmatched_actual_records"] = unmatchedActual;
            return result;
        }

        for (int expectedIndex = 0; expectedIndex < expectedRecords.Count; expectedIndex++)
        {
            Dictionary<string, object> expected = expectedRecords[expectedIndex];
            int matchIndex = -1;
            for (int actualIndex = 0; actualIndex < actualRecords.Count; actualIndex++)
            {
                if (consumed[actualIndex]) continue;
                Dictionary<string, object> equivalenceEvidence;
                if (F12GeometryRecordsEquivalent(expected, actualRecords[actualIndex], toleranceMm, out equivalenceEvidence))
                {
                    matchIndex = actualIndex;
                    break;
                }
            }
            if (matchIndex < 0)
            {
                unmatchedExpected.Add(expected == null || !expected.ContainsKey("source_jid") ? null : expected["source_jid"]);
                continue;
            }
            consumed[matchIndex] = true;
            Dictionary<string, object> pair = new Dictionary<string, object>(StringComparer.Ordinal);
            pair["expected_source_jid"] = expected.ContainsKey("source_jid") ? expected["source_jid"] : null;
            pair["actual_source_jid"] = actualRecords[matchIndex].ContainsKey("source_jid") ? actualRecords[matchIndex]["source_jid"] : null;
            if (actualRecords[matchIndex].ContainsKey("section_output_jid"))
                pair["section_output_jid"] = actualRecords[matchIndex]["section_output_jid"];
            pair["actual_record_index"] = matchIndex;
            Dictionary<string, object> pairEvidence;
            F12GeometryRecordsEquivalent(expected, actualRecords[matchIndex], toleranceMm, out pairEvidence);
            pair["geometry_match"] = pairEvidence.ContainsKey("match_mode") ? pairEvidence["match_mode"] : "GEOMETRY_EQUIVALENT";
            pair["geometry_match_evidence"] = pairEvidence;
            pairs.Add(pair);
        }
        for (int i = 0; i < actualRecords.Count; i++)
            if (!consumed[i]) unmatchedActual.Add(actualRecords[i]);
        result["status"] = unmatchedExpected.Count == 0 && unmatchedActual.Count == 0 ? "MATCHED" : "MISMATCH";
        result["matched_geometry_pairs"] = pairs;
        result["unmatched_expected_records"] = unmatchedExpected;
        result["unmatched_actual_records"] = unmatchedActual;
        result["expected_record_count"] = expectedRecords.Count;
        result["actual_record_count"] = actualRecords.Count;
        return result;
    }

    /// <summary>Compares two geometry records without calling NXOpen.</summary>
    public static bool F12GeometryRecordsEquivalent(Dictionary<string, object> expectedRecord,
        Dictionary<string, object> actualRecord, double toleranceMm, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence["tolerance_mm"] = toleranceMm;
        if (!IsFinite(toleranceMm) || toleranceMm < 0.0)
        {
            evidence["status"] = "INVALID_TOLERANCE";
            return false;
        }
        bool equivalent;
        try { equivalent = F12GeometryRecordMatches(expectedRecord, actualRecord, toleranceMm); }
        catch { equivalent = false; }
        evidence["status"] = equivalent ? "EQUIVALENT" : "NOT_EQUIVALENT";
        Dictionary<string, object> expectedGeometry = expectedRecord != null && expectedRecord.ContainsKey("geometry")
            ? expectedRecord["geometry"] as Dictionary<string, object> : expectedRecord;
        evidence["kind"] = expectedGeometry == null ? null : expectedGeometry.ContainsKey("kind")
            ? expectedGeometry["kind"] : null;
        if (equivalent)
        {
            string kind = expectedGeometry == null ? null : F12String(expectedGeometry, "kind");
            evidence["match_mode"] = kind == "LINE" ? "UNORDERED_ENDPOINTS" :
                kind == "ARC" ? "CIRCLE_SUPPORT_AND_ORIENTATION_INSENSITIVE_DOMAIN" :
                kind == "ELLIPSE" ? "ELLIPSE_SUPPORT_AND_ORIENTATION_INSENSITIVE_DOMAIN" :
                kind == "SPLINE" ? "CARTESIAN_POLES_POSITIVE_RELATIVE_WEIGHTS_KNOTS_AND_DOMAIN_SAMPLES" : "EXACT_DOMAIN";
            if (kind == "SPLINE")
            {
                evidence["rational_pole_coordinates"] = "HOMOGENEOUS_XYZ_DIVIDED_BY_POSITIVE_W";
                evidence["weight_comparison"] = "GLOBAL_SCALE_NORMALIZED_RELATIVE_TOLERANCE_1E-12";
                evidence["normalized_knot_tolerance"] = 1.0e-12;
            }
        }
        else evidence["reason"] = "GEOMETRY_KIND_OR_COMPLETE_DOMAIN_MISMATCH";
        return equivalent;
    }

    private static bool F12GeometryRecordMatches(Dictionary<string, object> expectedRecord,
        Dictionary<string, object> actualRecord, double toleranceMm)
    {
        if (expectedRecord == null || actualRecord == null) return false;
        Dictionary<string, object> expected = expectedRecord.ContainsKey("geometry")
            ? expectedRecord["geometry"] as Dictionary<string, object> : expectedRecord;
        Dictionary<string, object> actual = actualRecord.ContainsKey("geometry")
            ? actualRecord["geometry"] as Dictionary<string, object> : actualRecord;
        if (expected == null || actual == null) return false;
        string expectedKind = F12String(expected, "kind");
        if (!String.Equals(expectedKind, F12String(actual, "kind"), StringComparison.Ordinal)) return false;
        if (expectedKind == "LINE")
        {
            double[] expectedStart = F12Vector(expected, "start");
            double[] expectedEnd = F12Vector(expected, "end");
            double[] actualStart = F12Vector(actual, "start");
            double[] actualEnd = F12Vector(actual, "end");
            return (F12PointsNear(expectedStart, actualStart, toleranceMm) && F12PointsNear(expectedEnd, actualEnd, toleranceMm)) ||
                (F12PointsNear(expectedStart, actualEnd, toleranceMm) && F12PointsNear(expectedEnd, actualStart, toleranceMm));
        }
        if (expectedKind == "ARC")
        {
            double expectedRadius = F12Number(expected, "radius_mm");
            double actualRadius = F12Number(actual, "radius_mm");
            if (!F12PointsNear(F12Vector(expected, "center"), F12Vector(actual, "center"), toleranceMm) ||
                !IsFinite(expectedRadius) || !IsFinite(actualRadius) ||
                Math.Abs(expectedRadius - actualRadius) > toleranceMm ||
                !F12DirectionsParallel(F12Vector(expected, "normal"), F12Vector(actual, "normal"))) return false;
            return F12ConicDomainSamplesMatch(expected, actual, toleranceMm);
        }
        if (expectedKind == "ELLIPSE")
        {
            double expectedMajor = F12Number(expected, "major_radius_mm");
            double actualMajor = F12Number(actual, "major_radius_mm");
            double expectedMinor = F12Number(expected, "minor_radius_mm");
            double actualMinor = F12Number(actual, "minor_radius_mm");
            if (!F12PointsNear(F12Vector(expected, "center"), F12Vector(actual, "center"), toleranceMm) ||
                !IsFinite(expectedMajor) || !IsFinite(actualMajor) || !IsFinite(expectedMinor) || !IsFinite(actualMinor) ||
                Math.Abs(expectedMajor - actualMajor) > toleranceMm ||
                Math.Abs(expectedMinor - actualMinor) > toleranceMm ||
                !F12DirectionsParallel(F12Vector(expected, "normal"), F12Vector(actual, "normal"))) return false;
            double major = F12Number(expected, "major_radius_mm");
            double minor = F12Number(expected, "minor_radius_mm");
            if (Math.Abs(major - minor) > toleranceMm &&
                !F12DirectionsParallel(F12Vector(expected, "major_axis"), F12Vector(actual, "major_axis"))) return false;
            return F12ConicDomainSamplesMatch(expected, actual, toleranceMm);
        }
        if (expectedKind == "SPLINE")
            return F12NurbsDefinitionsMatch(expected, actual, toleranceMm) &&
                F12SplineDomainSamplesMatch(expected, actual, toleranceMm);
        return false;
    }

    private static bool F12ConicDomainSamplesMatch(Dictionary<string, object> expected,
        Dictionary<string, object> actual, double toleranceMm)
    {
        bool expectedClosed = F12Boolean(expected, "is_closed_domain");
        bool actualClosed = F12Boolean(actual, "is_closed_domain");
        if (expectedClosed || actualClosed) return expectedClosed && actualClosed;
        IList expectedSamples = expected.ContainsKey("domain_samples") ? expected["domain_samples"] as IList : null;
        IList actualSamples = actual.ContainsKey("domain_samples") ? actual["domain_samples"] as IList : null;
        if (expectedSamples == null || actualSamples == null || expectedSamples.Count != 5 || actualSamples.Count != 5)
            return false;
        bool forward = true;
        bool reverse = true;
        for (int i = 0; i < 5; i++)
        {
            forward = forward && F12PointsNear(expectedSamples[i] as double[], actualSamples[i] as double[], toleranceMm);
            reverse = reverse && F12PointsNear(expectedSamples[i] as double[], actualSamples[4 - i] as double[], toleranceMm);
        }
        return forward || reverse;
    }

    private static bool F12NurbsDefinitionsMatch(Dictionary<string, object> expected,
        Dictionary<string, object> actual, double toleranceMm)
    {
        Dictionary<string, object> first = expected.ContainsKey("nurbs_definition")
            ? expected["nurbs_definition"] as Dictionary<string, object> : null;
        Dictionary<string, object> second = actual.ContainsKey("nurbs_definition")
            ? actual["nurbs_definition"] as Dictionary<string, object> : null;
        if (first == null || second == null || F12Integer(first, "order") != F12Integer(second, "order") ||
            F12Integer(first, "pole_count") != F12Integer(second, "pole_count") ||
            F12Boolean(first, "rational") != F12Boolean(second, "rational")) return false;
        IList firstPoles = first.ContainsKey("poles") ? first["poles"] as IList : null;
        IList secondPoles = second.ContainsKey("poles") ? second["poles"] as IList : null;
        IList firstKnots = first.ContainsKey("knots") ? first["knots"] as IList : null;
        IList secondKnots = second.ContainsKey("knots") ? second["knots"] as IList : null;
        int poleCount = F12Integer(first, "pole_count");
        int order = F12Integer(first, "order");
        if (firstPoles == null || secondPoles == null || firstKnots == null || secondKnots == null ||
            poleCount <= 0 || order <= 0 || firstPoles.Count != poleCount || secondPoles.Count != poleCount ||
            firstKnots.Count != poleCount + order || secondKnots.Count != poleCount + order) return false;

        double[] normalizedFirstKnots;
        double[] normalizedSecondKnots;
        double firstStart, firstEnd, secondStart, secondEnd;
        double[] firstWeights, secondWeights;
        bool rational = F12Boolean(first, "rational");
        if (!F12TryNormalizeKnots(firstKnots, out normalizedFirstKnots) ||
            !F12TryNormalizeKnots(secondKnots, out normalizedSecondKnots) ||
            !F12TryNormalizeSplineInterval(first, firstKnots, out firstStart, out firstEnd) ||
            !F12TryNormalizeSplineInterval(second, secondKnots, out secondStart, out secondEnd) ||
            !F12TryGetNormalizedSplineWeights(firstPoles, rational, out firstWeights) ||
            !F12TryGetNormalizedSplineWeights(secondPoles, rational, out secondWeights)) return false;
        return F12NurbsOrientationMatches(firstPoles, normalizedFirstKnots, firstStart, firstEnd, firstWeights,
                secondPoles, normalizedSecondKnots, secondStart, secondEnd, secondWeights,
                rational, false, toleranceMm) ||
            F12NurbsOrientationMatches(firstPoles, normalizedFirstKnots, firstStart, firstEnd, firstWeights,
                secondPoles, normalizedSecondKnots, secondStart, secondEnd, secondWeights,
                rational, true, toleranceMm);
    }

    private static bool F12NurbsOrientationMatches(IList firstPoles, double[] firstKnots,
        double firstStart, double firstEnd, double[] firstWeights,
        IList secondPoles, double[] secondKnots, double secondStart, double secondEnd,
        double[] secondWeights, bool rational, bool reversed, double toleranceMm)
    {
        int poleCount = firstPoles.Count;
        for (int i = 0; i < poleCount; i++)
        {
            IList firstPole = firstPoles[i] as IList;
            IList secondPole = secondPoles[reversed ? poleCount - 1 - i : i] as IList;
            if (firstPole == null || secondPole == null || firstPole.Count < (rational ? 4 : 3) ||
                secondPole.Count < (rational ? 4 : 3)) return false;
            double[] firstCartesian;
            double[] secondCartesian;
            if (!F12TryCartesianPole(firstPole, rational, out firstCartesian) ||
                !F12TryCartesianPole(secondPole, rational, out secondCartesian) ||
                !F12PointsNear(firstCartesian, secondCartesian, toleranceMm)) return false;
            if (rational)
            {
                int secondIndex = reversed ? poleCount - 1 - i : i;
                double firstWeight = firstWeights[i];
                double secondWeight = secondWeights[secondIndex];
                double weightScale = Math.Max(Math.Abs(firstWeight), Math.Abs(secondWeight));
                // A raw homogeneous numerator delta of 1e-7 at W=1e-9 is a
                // 100 mm Cartesian control-point delta, not a 1e-7 mm delta.
                if (!IsFinite(weightScale) || weightScale <= 0.0 ||
                    Math.Abs(firstWeight - secondWeight) / weightScale > 1.0e-12) return false;
            }
        }
        if (firstKnots == null || secondKnots == null || firstKnots.Length != secondKnots.Length) return false;
        int knotCount = firstKnots.Length;
        for (int i = 0; i < knotCount; i++)
        {
            double secondKnot = secondKnots[reversed ? knotCount - 1 - i : i];
            if (reversed) secondKnot = 1.0 - secondKnot;
            if (!IsFinite(firstKnots[i]) || !IsFinite(secondKnot) ||
                Math.Abs(firstKnots[i] - secondKnot) > 1.0e-12) return false;
        }
        double expectedStart = reversed ? 1.0 - secondEnd : secondStart;
        double expectedEnd = reversed ? 1.0 - secondStart : secondEnd;
        return Math.Abs(firstStart - expectedStart) <= 1.0e-12 &&
            Math.Abs(firstEnd - expectedEnd) <= 1.0e-12;
    }

    private static bool F12SplineDomainSamplesMatch(Dictionary<string, object> expected,
        Dictionary<string, object> actual, double toleranceMm)
    {
        IList first = expected.ContainsKey("domain_samples") ? expected["domain_samples"] as IList : null;
        IList second = actual.ContainsKey("domain_samples") ? actual["domain_samples"] as IList : null;
        if (first == null && second == null) return true;
        if (first == null || second == null || first.Count != 5 || second.Count != 5) return false;
        bool forward = true;
        bool reverse = true;
        for (int i = 0; i < 5; i++)
        {
            forward = forward && F12PointsNear(first[i] as double[], second[i] as double[], toleranceMm);
            reverse = reverse && F12PointsNear(first[i] as double[], second[4 - i] as double[], toleranceMm);
        }
        return forward || reverse;
    }

    private static string F12String(Dictionary<string, object> record, string key)
    {
        object value;
        return record.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static double F12Number(Dictionary<string, object> record, string key)
    {
        object value;
        if (!record.TryGetValue(key, out value) || value == null) return Double.NaN;
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static int F12Integer(Dictionary<string, object> record, string key)
    {
        object value;
        if (!record.TryGetValue(key, out value) || value == null) return Int32.MinValue;
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static bool F12Boolean(Dictionary<string, object> record, string key)
    {
        object value;
        return record.TryGetValue(key, out value) && value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private static double[] F12Vector(Dictionary<string, object> record, string key)
    {
        object value;
        IList list = record.TryGetValue(key, out value) ? value as IList : null;
        if (list == null || list.Count != 3) return null;
        return new double[] {
            Convert.ToDouble(list[0], CultureInfo.InvariantCulture),
            Convert.ToDouble(list[1], CultureInfo.InvariantCulture),
            Convert.ToDouble(list[2], CultureInfo.InvariantCulture) };
    }

    private static bool F12PointsNear(double[] first, double[] second, double tolerance)
    {
        if (first == null || second == null || first.Length != 3 || second.Length != 3) return false;
        double x = first[0] - second[0];
        double y = first[1] - second[1];
        double z = first[2] - second[2];
        return Math.Sqrt(x * x + y * y + z * z) <= tolerance;
    }

    private static bool F12DirectionsParallel(double[] first, double[] second)
    {
        if (first == null || second == null || first.Length != 3 || second.Length != 3) return false;
        Vector3d a = new Vector3d(first[0], first[1], first[2]);
        Vector3d b = new Vector3d(second[0], second[1], second[2]);
        double aMagnitude = Magnitude(a);
        double bMagnitude = Magnitude(b);
        if (aMagnitude <= 0.0 || bMagnitude <= 0.0) return false;
        return Magnitude(Cross(Scale(a, 1.0 / aMagnitude), Scale(b, 1.0 / bMagnitude))) <= DirectionTolerance;
    }

    /// <summary>
    /// Analyzes native sketch curves. Length and endpoint tolerances are 1e-6 mm;
    /// parallel-direction tests use a dimensionless 1e-7 cross-product tolerance.
    /// </summary>
    public static Dictionary<string, object> Analyze(Part part, Sketch sketch, bool classifySections)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (sketch == null) throw new ArgumentNullException("sketch");

        Dictionary<string, object> report = NewReport();
        List<string> notes = (List<string>)report["notes"];
        List<CurveInfo> curves = new List<CurveInfo>();
        bool unavailable = false;
        bool degenerate = false;
        bool selfIntersection = false;
        bool nativeFailed = false;
        bool splineFailed = false;
        bool hasSpline = false;
        int graphOpen = 0;
        int graphClosed = 0;
        bool endpointsComplete = true;
        double mmPerPartUnit;
        double tolerance;

        if (!TryUnitScale(part, out mmPerPartUnit))
        {
            AddNote(notes, "unsupported_or_mixed_part_units");
            Set(report, "status", "ANALYSIS_UNAVAILABLE");
            Set(report, "native_intersection_check", "UNAVAILABLE");
            Set(report, "spline_self_intersection_check", "NOT_APPLICABLE");
            return report;
        }
        tolerance = EndpointToleranceMm / mmPerPartUnit;

        try
        {
            NXObject[] allGeometry = sketch.GetAllGeometry();
            if (allGeometry == null)
                throw new InvalidOperationException("Sketch.GetAllGeometry returned null.");
            if (allGeometry != null)
            {
                for (int i = 0; i < allGeometry.Length; i++)
                {
                    Curve curve = allGeometry[i] as Curve;
                    if (curve == null) continue;
                    CurveInfo info = new CurveInfo();
                    info.Curve = curve;
                    info.Id = curve.JournalIdentifier;
                    info.Supported = curve is Line || curve is Arc || curve is Ellipse || curve is Spline;
                    curves.Add(info);
                    if (String.IsNullOrEmpty(info.Id))
                    {
                        unavailable = true;
                        AddNote(notes, "native_geometry_journal_id_missing");
                    }
                    if (!info.Supported)
                    {
                        unavailable = true;
                        AddNote(notes, "unsupported_native_curve_type");
                    }
                }
            }
            Set(report, "curve_count", curves.Count);
            if (curves.Count == 0)
            {
                Set(report, "status", "EMPTY");
                Set(report, "native_intersection_check", "NOT_REQUIRED");
                Set(report, "spline_self_intersection_check", "NOT_APPLICABLE");
                return report;
            }
            HashSet<string> geometryIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < curves.Count; i++)
            {
                if (String.IsNullOrEmpty(curves[i].Id) || !geometryIds.Add(curves[i].Id))
                {
                    unavailable = true;
                    AddNote(notes, "native_geometry_journal_id_missing_or_duplicate");
                    throw new InvalidOperationException("Sketch curves did not have unique JournalIdentifier values.");
                }
            }

            UFSession uf = UFSession.GetUFSession();
            for (int i = 0; i < curves.Count; i++)
            {
                CurveInfo info = curves[i];
                try
                {
                    uf.Eval.Initialize(info.Curve.Tag, out info.Evaluator);
                    if (info.Evaluator == IntPtr.Zero)
                        throw new InvalidOperationException("UF evaluator initialization returned a null handle.");
                    double[] limits = new double[2];
                    uf.Eval.AskLimits(info.Evaluator, limits);
                    info.StartParameter = limits[0];
                    info.EndParameter = limits[1];
                    info.Start = EvaluatePoint(uf, info.Curve, info.StartParameter);
                    info.End = EvaluatePoint(uf, info.Curve, info.EndParameter);
                    info.Length = info.Curve.GetLength();
                    info.Closed = Distance(info.Start, info.End) <= tolerance;
                    info.EndpointValid = IsFinite(info.Start.X) && IsFinite(info.Start.Y) && IsFinite(info.Start.Z) &&
                        IsFinite(info.End.X) && IsFinite(info.End.Y) && IsFinite(info.End.Z) && IsFinite(info.Length);
                    if (!info.EndpointValid)
                    {
                        endpointsComplete = false;
                        unavailable = true;
                        AddNote(notes, "native_curve_endpoint_or_length_unavailable");
                    }
                    else if (info.Length <= tolerance)
                    {
                        degenerate = true;
                        AddNote(notes, "curve_length_at_or_below_1e-6_mm");
                    }
                    if (CurveIsSpline(info)) hasSpline = true;
                }
                catch
                {
                    endpointsComplete = false;
                    unavailable = true;
                    AddNote(notes, "native_curve_endpoint_or_length_unavailable");
                }
            }

            if (endpointsComplete)
            {
                bool branch;
                BuildEndpointGraph(curves, tolerance, out graphOpen, out graphClosed, out branch);
                Set(report, "open_chain_count", graphOpen);
                Set(report, "closed_loop_count", graphClosed);
                if (branch)
                {
                    selfIntersection = true;
                    AddNote(notes, "endpoint_graph_branch_detected");
                }
            }
            else
            {
                Set(report, "open_chain_count", 0);
                Set(report, "closed_loop_count", 0);
            }

            for (int i = 0; i < curves.Count; i++)
            {
                Spline spline = curves[i].Curve as Spline;
                if (spline == null) continue;
                hasSpline = true;
                try
                {
                    bool intersectsItself;
                    uf.Curve.IsSplineSelfInt(spline.Tag, out intersectsItself);
                    if (intersectsItself)
                    {
                        selfIntersection = true;
                        AddNote(notes, "native_spline_self_intersection_detected");
                    }
                }
                catch
                {
                    splineFailed = true;
                    unavailable = true;
                    AddNote(notes, "native_spline_self_intersection_check_failed");
                }
            }

            List<object> pairs = (List<object>)report["intersection_pairs"];
            for (int i = 0; i < curves.Count; i++)
            {
                for (int j = i + 1; j < curves.Count; j++)
                {
                    CurveInfo first = curves[i];
                    CurveInfo second = curves[j];
                    Dictionary<string, object> pair = new Dictionary<string, object>();
                    pair["geometry1_id"] = first.Id;
                    pair["geometry2_id"] = second.Id;
                    List<object> points = new List<object>();
                    pair["points"] = points;
                    int count = -1;
                    bool pairHasInteriorIntersection = false;
                    try
                    {
                        double[] data;
                        uf.Modl.IntersectCurveToCurve(first.Curve.Tag, second.Curve.Tag, out count, out data);
                        if (count < 0 || (count > 0 && (data == null || data.Length < count * 5)))
                            throw new InvalidOperationException("Native intersection output was incomplete.");
                        for (int k = 0; k < count; k++)
                        {
                            int offset = k * 5;
                            double x = data[offset];
                            double y = data[offset + 1];
                            double z = data[offset + 2];
                            points.Add(new double[] { x * mmPerPartUnit, y * mmPerPartUnit, z * mmPerPartUnit,
                                data[offset + 3], data[offset + 4] });
                            Point3d intersection = new Point3d(x, y, z);
                            bool endpointOnFirst = IsOpenEndpoint(first, intersection, tolerance);
                            bool endpointOnSecond = IsOpenEndpoint(second, intersection, tolerance);
                            if (!endpointOnFirst || !endpointOnSecond)
                            {
                                pairHasInteriorIntersection = true;
                                selfIntersection = true;
                                AddNote(notes, "native_curve_intersection_not_at_two_open_endpoints");
                            }
                        }
                    }
                    catch
                    {
                        nativeFailed = true;
                        unavailable = true;
                        AddNote(notes, "native_curve_intersection_check_failed");
                    }
                    pair["count"] = count;
                    pairs.Add(pair);

                    if (first.Curve is Line && second.Curve is Line)
                    {
                        if (HasCollinearLineOverlap(first, second, tolerance))
                        {
                            selfIntersection = true;
                            AddNote(notes, "collinear_line_interval_overlap_detected");
                        }
                    }
                    else if (!pairHasInteriorIntersection)
                    {
                        try
                        {
                            bool overlap = IsConic(first) && IsConic(second)
                                ? HasConicOverlap(first, second, tolerance)
                                : HasCoincidentOverlap(uf, first, second, tolerance);
                            if (overlap)
                            {
                                selfIntersection = true;
                                AddNote(notes, IsConic(first) && IsConic(second)
                                    ? "native_conic_interval_overlap_detected"
                                    : "native_closest_point_overlap_detected");
                            }
                        }
                        catch
                        {
                            nativeFailed = true;
                            unavailable = true;
                            AddNote(notes, "native_closest_point_overlap_check_failed");
                        }
                    }
                }
            }

            if (hasSpline && !splineFailed) Set(report, "spline_self_intersection_check", "VERIFIED");
            else if (hasSpline) Set(report, "spline_self_intersection_check", "FAILED");
            else Set(report, "spline_self_intersection_check", "NOT_APPLICABLE");
            Set(report, "native_intersection_check", nativeFailed ? "FAILED" : "VERIFIED");

            int outerLoops = 0;
            int innerLoops = 0;
            int sectionLoops = 0;
            bool sectionClassificationSucceeded = false;
            bool graphHasClosedLoops = endpointsComplete && graphClosed > 0;
            if (classifySections && graphHasClosedLoops && !selfIntersection && !degenerate &&
                !nativeFailed && !splineFailed && !unavailable)
            {
                List<NXObject> closedCurves = new List<NXObject>();
                for (int i = 0; i < curves.Count; i++)
                    if (curves[i].InClosedGraphComponent) closedCurves.Add(curves[i].Curve);
                string sectionNote;
                sectionClassificationSucceeded = TryClassifySections(part, uf, closedCurves, tolerance,
                    graphClosed, out outerLoops, out innerLoops, out sectionLoops, out sectionNote);
                if (sectionNote != null) AddNote(notes, sectionNote);
            }
            else if (classifySections && !graphHasClosedLoops)
            {
                sectionClassificationSucceeded = true;
            }
            else if (!classifySections && graphHasClosedLoops)
            {
                AddNote(notes, "section_classification_not_requested");
            }

            Set(report, "outer_loop_count", outerLoops);
            Set(report, "inner_loop_count", innerLoops);
            Set(report, "section_loop_count", sectionLoops);

            if (selfIntersection)
                Set(report, "status", "INVALID_SELF_INTERSECTION");
            else if (degenerate)
                Set(report, "status", "INVALID_DEGENERATE");
            else if (unavailable || nativeFailed || splineFailed || !endpointsComplete)
                Set(report, "status", "ANALYSIS_UNAVAILABLE");
            else if (graphOpen > 0)
                Set(report, "status", "OPEN_GEOMETRY");
            else if (!classifySections || !sectionClassificationSucceeded)
                Set(report, "status", "ANALYSIS_UNAVAILABLE");
            else
                Set(report, "status", "VALID_CLOSED_PROFILES");

            if (classifySections && sectionClassificationSucceeded && graphHasClosedLoops &&
                !selfIntersection && !degenerate && !nativeFailed && !splineFailed && !unavailable)
                AddNote(notes, "temporary_sections_undone_and_part_inventory_and_dirty_state_verified");
        }
        catch (SectionCleanupException)
        {
            throw;
        }
        catch
        {
            unavailable = true;
            AddNote(notes, "profile_analysis_native_api_failed");
            Set(report, "status", "ANALYSIS_UNAVAILABLE");
            if (String.Equals((string)report["native_intersection_check"], "NOT_REQUIRED", StringComparison.Ordinal))
                Set(report, "native_intersection_check", "UNAVAILABLE");
            if (String.Equals((string)report["spline_self_intersection_check"], "NOT_APPLICABLE", StringComparison.Ordinal) && hasSpline)
                Set(report, "spline_self_intersection_check", "FAILED");
        }
        finally
        {
            UFSession uf = null;
            try { uf = UFSession.GetUFSession(); } catch { }
            if (uf != null)
            {
                for (int i = 0; i < curves.Count; i++)
                {
                    if (curves[i].Evaluator == IntPtr.Zero) continue;
                    try { uf.Eval.Free(curves[i].Evaluator); }
                    catch
                    {
                        AddNote(notes, "native_evaluator_free_failed");
                        Set(report, "status", "ANALYSIS_UNAVAILABLE");
                        Set(report, "native_intersection_check", "FAILED");
                    }
                    curves[i].Evaluator = IntPtr.Zero;
                }
            }
        }

        if (String.Equals((string)report["status"], "ANALYSIS_UNAVAILABLE", StringComparison.Ordinal) &&
            String.Equals((string)report["native_intersection_check"], "NOT_REQUIRED", StringComparison.Ordinal))
            Set(report, "native_intersection_check", "UNAVAILABLE");
        return report;
    }

    private static Dictionary<string, object> NewReport()
    {
        Dictionary<string, object> report = new Dictionary<string, object>();
        report["status"] = "ANALYSIS_UNAVAILABLE";
        report["curve_count"] = 0;
        report["open_chain_count"] = 0;
        report["closed_loop_count"] = 0;
        report["outer_loop_count"] = 0;
        report["inner_loop_count"] = 0;
        report["section_loop_count"] = 0;
        report["native_intersection_check"] = "NOT_REQUIRED";
        report["spline_self_intersection_check"] = "NOT_APPLICABLE";
        report["intersection_pairs"] = new List<object>();
        report["notes"] = new List<string>();
        return report;
    }

    private static void Set(Dictionary<string, object> report, string name, object value)
    {
        report[name] = value;
    }

    private static void AddNote(List<string> notes, string note)
    {
        if (!notes.Contains(note)) notes.Add(note);
    }

    private static bool CurveIsSpline(CurveInfo info)
    {
        return info.Curve is Spline;
    }

    private static bool TryUnitScale(Part part, out double mmPerPartUnit)
    {
        mmPerPartUnit = 1.0;
        return part.PartUnits == BasePart.Units.Millimeters;
    }

    private static Point3d EvaluatePoint(UFSession uf, Curve curve, double parameter)
    {
        double[] result = new double[3];
        uf.Curve.EvaluateCurve(curve.Tag, parameter, 0, result);
        return new Point3d(result[0], result[1], result[2]);
    }

    private static void BuildEndpointGraph(List<CurveInfo> curves, double tolerance,
        out int openChains, out int closedLoops, out bool branch)
    {
        int endpointCount = curves.Count * 2;
        int[] vertexParent = new int[endpointCount];
        int[] componentParent = new int[endpointCount];
        for (int i = 0; i < endpointCount; i++)
        {
            vertexParent[i] = i;
            componentParent[i] = i;
        }

        // First collapse endpoint coordinates into graph vertices, including the two ends of closed curves.
        for (int i = 0; i < endpointCount; i++)
            for (int j = i + 1; j < endpointCount; j++)
                if (Distance(EndpointAt(curves, i), EndpointAt(curves, j)) <= tolerance)
                    Union(vertexParent, i, j);

        Dictionary<int, GraphComponent> components = new Dictionary<int, GraphComponent>();
        Dictionary<int, int> degree = new Dictionary<int, int>();
        for (int i = 0; i < curves.Count; i++)
        {
            int a = Find(vertexParent, i * 2);
            int b = Find(vertexParent, i * 2 + 1);
            // A second DSU follows curve edges to form connected graph components.
            Union(componentParent, a, b);
            Increment(degree, a);
            Increment(degree, b);
        }

        for (int i = 0; i < curves.Count; i++)
        {
            int a = Find(vertexParent, i * 2);
            int b = Find(vertexParent, i * 2 + 1);
            int componentKey = Find(componentParent, a);
            GraphComponent component;
            if (!components.TryGetValue(componentKey, out component))
            {
                component = new GraphComponent();
                components.Add(componentKey, component);
            }
            component.Vertices.Add(a);
            component.Vertices.Add(b);
            component.Curves.Add(i);
        }

        openChains = 0;
        closedLoops = 0;
        branch = false;
        foreach (GraphComponent component in components.Values)
        {
            bool simpleCycle = true;
            foreach (int vertex in component.Vertices)
            {
                int value;
                degree.TryGetValue(vertex, out value);
                if (value > 2) branch = true;
                if (value != 2) simpleCycle = false;
            }
            component.Closed = simpleCycle;
            if (simpleCycle) closedLoops++;
            else openChains++;
            for (int i = 0; i < component.Curves.Count; i++)
                curves[component.Curves[i]].InClosedGraphComponent = simpleCycle;
        }
    }

    private static Point3d EndpointAt(List<CurveInfo> curves, int endpointIndex)
    {
        CurveInfo curve = curves[endpointIndex / 2];
        return endpointIndex % 2 == 0 ? curve.Start : curve.End;
    }

    private static void Increment(Dictionary<int, int> values, int key)
    {
        int value;
        values.TryGetValue(key, out value);
        values[key] = value + 1;
    }

    private static int Find(int[] parent, int value)
    {
        int root = value;
        while (parent[root] != root) root = parent[root];
        while (parent[value] != value)
        {
            int next = parent[value];
            parent[value] = root;
            value = next;
        }
        return root;
    }

    private static void Union(int[] parent, int first, int second)
    {
        int a = Find(parent, first);
        int b = Find(parent, second);
        if (a != b) parent[b] = a;
    }

    private static bool IsOpenEndpoint(CurveInfo curve, Point3d point, double tolerance)
    {
        if (curve.Closed || !curve.EndpointValid) return false;
        return Distance(point, curve.Start) <= tolerance || Distance(point, curve.End) <= tolerance;
    }

    private static bool HasCollinearLineOverlap(CurveInfo first, CurveInfo second, double tolerance)
    {
        Vector3d direction = Subtract(first.End, first.Start);
        Vector3d other = Subtract(second.End, second.Start);
        double length = Magnitude(direction);
        double otherLength = Magnitude(other);
        if (length <= tolerance || otherLength <= tolerance) return false;
        Vector3d unit = Scale(direction, 1.0 / length);
        Vector3d otherUnit = Scale(other, 1.0 / otherLength);
        if (Magnitude(Cross(unit, otherUnit)) > DirectionTolerance) return false;
        if (Magnitude(Cross(Subtract(second.Start, first.Start), unit)) > tolerance ||
            Magnitude(Cross(Subtract(second.End, first.Start), unit)) > tolerance) return false;
        double secondStart = Dot(Subtract(second.Start, first.Start), unit);
        double secondEnd = Dot(Subtract(second.End, first.Start), unit);
        double low = Math.Max(0.0, Math.Min(secondStart, secondEnd));
        double high = Math.Min(length, Math.Max(secondStart, secondEnd));
        return high - low > tolerance;
    }

    private static bool IsConic(CurveInfo curve)
    {
        return curve.Curve is Arc || curve.Curve is Ellipse;
    }

    /// <summary>Detects positive-length overlap on a common native circle or ellipse support.</summary>
    private static bool HasConicOverlap(CurveInfo first, CurveInfo second, double tolerance)
    {
        ConicInfo a = GetConicInfo(first);
        ConicInfo b = GetConicInfo(second);
        double aRadius = Magnitude(a.AxisA);
        double bRadius = Magnitude(a.AxisB);
        double aOtherRadius = Magnitude(b.AxisA);
        double bOtherRadius = Magnitude(b.AxisB);
        if (aRadius <= tolerance || bRadius <= tolerance ||
            Math.Abs(aRadius - aOtherRadius) > tolerance || Math.Abs(bRadius - bOtherRadius) > tolerance ||
            Distance(a.Center, b.Center) > tolerance)
            return false;

        Vector3d axisA = Scale(a.AxisA, 1.0 / aRadius);
        Vector3d axisB = Scale(a.AxisB, 1.0 / bRadius);
        double cosine = Dot(b.AxisA, axisA) / aRadius;
        double sine = Dot(b.AxisA, axisB) / bRadius;
        double phaseMagnitude = Math.Sqrt(cosine * cosine + sine * sine);
        if (Math.Abs(phaseMagnitude - 1.0) > DirectionTolerance ||
            DistanceVector(b.AxisA, Add(Scale(axisA, aRadius * cosine), Scale(axisB, bRadius * sine))) > tolerance)
            return false;

        double tangentCosine = Dot(b.AxisB, axisA) / aRadius;
        double tangentSine = Dot(b.AxisB, axisB) / bRadius;
        double expectedTangentCosine = -sine / phaseMagnitude;
        double expectedTangentSine = cosine / phaseMagnitude;
        double orientation = tangentCosine * expectedTangentCosine + tangentSine * expectedTangentSine;
        if (Math.Abs(Math.Abs(orientation) - 1.0) > DirectionTolerance) return false;
        Vector3d expectedAxisB = Scale(Add(Scale(axisA, expectedTangentCosine * aRadius),
            Scale(axisB, expectedTangentSine * bRadius)), orientation < 0.0 ? -1.0 : 1.0);
        if (DistanceVector(b.AxisB, expectedAxisB) > tolerance) return false;

        double phase = Math.Atan2(sine, cosine);
        double mappedSweep = (orientation < 0.0 ? -1.0 : 1.0) * (b.End - b.Start);
        double angleTolerance = tolerance / Math.Min(aRadius, bRadius);
        List<AngularSegment> firstSegments = CircularSegments(a.Start, a.End - a.Start, angleTolerance);
        List<AngularSegment> secondSegments = CircularSegments(phase +
            (orientation < 0.0 ? -1.0 : 1.0) * b.Start, mappedSweep, angleTolerance);
        double overlap = 0.0;
        for (int i = 0; i < firstSegments.Count; i++)
            for (int j = 0; j < secondSegments.Count; j++)
                overlap += Math.Max(0.0, Math.Min(firstSegments[i].End, secondSegments[j].End) -
                    Math.Max(firstSegments[i].Start, secondSegments[j].Start));
        return overlap > angleTolerance;
    }

    private static ConicInfo GetConicInfo(CurveInfo info)
    {
        Arc arc = info.Curve as Arc;
        if (arc != null)
        {
            Point3d center;
            Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            return new ConicInfo
            {
                Center = center,
                AxisA = Scale(xAxis, arc.Radius),
                AxisB = Scale(yAxis, arc.Radius),
                Start = info.StartParameter,
                End = info.EndParameter
            };
        }

        Ellipse ellipse = info.Curve as Ellipse;
        if (ellipse == null) throw new InvalidOperationException("Native curve is not an Arc or Ellipse.");
        double major, minor, polarStart, polarEnd, rotation;
        Point3d ellipseCenter;
        ellipse.GetParameters(out major, out minor, out polarStart, out polarEnd, out rotation, out ellipseCenter);
        if (!IsFinite(polarStart) || !IsFinite(polarEnd))
            throw new InvalidOperationException("Native ellipse polar angle data was non-finite.");
        Point3d frameCenter;
        Vector3d frameX, frameY;
        ellipse.GetOrientation(out frameCenter, out frameX, out frameY);
        double cosine = Math.Cos(rotation);
        double sine = Math.Sin(rotation);
        Vector3d majorAxis = Add(Scale(frameX, cosine), Scale(frameY, sine));
        Vector3d minorAxis = Add(Scale(frameX, -sine), Scale(frameY, cosine));
        return new ConicInfo
        {
            Center = ellipseCenter,
            AxisA = Scale(majorAxis, major),
            AxisB = Scale(minorAxis, minor),
            Start = info.StartParameter,
            End = info.EndParameter
        };
    }

    private static List<AngularSegment> CircularSegments(double start, double signedSweep, double angleTolerance)
    {
        List<AngularSegment> result = new List<AngularSegment>();
        double sweep = Math.Abs(signedSweep);
        if (sweep >= FullTurn - angleTolerance)
        {
            result.Add(new AngularSegment { Start = 0.0, End = FullTurn });
            return result;
        }
        if (sweep <= angleTolerance) return result;
        if (signedSweep < 0.0) start += signedSweep;
        start = NormalizeAngle(start);
        double end = start + sweep;
        if (end <= FullTurn)
            result.Add(new AngularSegment { Start = start, End = end });
        else
        {
            result.Add(new AngularSegment { Start = start, End = FullTurn });
            result.Add(new AngularSegment { Start = 0.0, End = end - FullTurn });
        }
        return result;
    }

    private static double NormalizeAngle(double angle)
    {
        double result = angle % FullTurn;
        if (result < 0.0) result += FullTurn;
        return result;
    }

    /// <summary>
    /// Detects a positive-length match with native closest-point evaluation. Spline inputs use
    /// NURBS knot spans and verify interior points between matched samples; the finite samples
    /// supply candidates and are not treated by themselves as proof of coincidence.
    /// </summary>
    private static bool HasCoincidentOverlap(UFSession uf, CurveInfo first, CurveInfo second, double tolerance)
    {
        if (first.Evaluator == IntPtr.Zero || second.Evaluator == IntPtr.Zero)
            throw new InvalidOperationException("UF evaluator was unavailable for the overlap guard.");
        List<CurveMatch> matches = new List<CurveMatch>();
        AddProjectedSamples(uf, first, second, tolerance, matches);
        if (HasContinuousMatch(uf, first, second, tolerance, matches)) return true;
        matches.Clear();
        AddProjectedSamples(uf, second, first, tolerance, matches);
        return HasContinuousMatch(uf, second, first, tolerance, matches);
    }

    private static void AddProjectedSamples(UFSession uf, CurveInfo source, CurveInfo target,
        double tolerance, List<CurveMatch> matches)
    {
        List<double> parameters = CurveSampleParameters(uf, source);
        for (int i = 0; i < parameters.Count; i++)
        {
            double sourceParameter = parameters[i];
            double[] sourceEval = new double[6];
            uf.Curve.EvaluateCurve(source.Curve.Tag, sourceParameter, 1, sourceEval);
            double[] sourcePoint = new double[] { sourceEval[0], sourceEval[1], sourceEval[2] };
            double targetParameter;
            double[] targetPoint = new double[3];
            uf.Eval.EvaluateClosestPoint(target.Evaluator, sourcePoint, out targetParameter, targetPoint);
            double dx = sourcePoint[0] - targetPoint[0];
            double dy = sourcePoint[1] - targetPoint[1];
            double dz = sourcePoint[2] - targetPoint[2];
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > tolerance) continue;

            double[] targetEval = new double[6];
            uf.Curve.EvaluateCurve(target.Curve.Tag, targetParameter, 1, targetEval);
            Vector3d sourceTangent = new Vector3d(sourceEval[3], sourceEval[4], sourceEval[5]);
            Vector3d targetTangent = new Vector3d(targetEval[3], targetEval[4], targetEval[5]);
            double sourceMagnitude = Magnitude(sourceTangent);
            double targetMagnitude = Magnitude(targetTangent);
            if (sourceMagnitude <= 0.0 || targetMagnitude <= 0.0) continue;
            double tangentDifference = Magnitude(Cross(Scale(sourceTangent, 1.0 / sourceMagnitude),
                Scale(targetTangent, 1.0 / targetMagnitude)));
            if (tangentDifference > DirectionTolerance) continue;
            CurveMatch match = new CurveMatch
            {
                SourceParameter = sourceParameter,
                TargetParameter = targetParameter,
                Point = new Point3d((sourcePoint[0] + targetPoint[0]) * 0.5,
                    (sourcePoint[1] + targetPoint[1]) * 0.5, (sourcePoint[2] + targetPoint[2]) * 0.5)
            };
            AddDistinctMatch(matches, match, source, target, tolerance);
        }
    }

    private static List<double> CurveSampleParameters(UFSession uf, CurveInfo curve)
    {
        List<double> breaks = new List<double>();
        breaks.Add(curve.StartParameter);
        breaks.Add(curve.EndParameter);
        Spline spline = curve.Curve as Spline;
        if (spline != null)
        {
            UFCurve.Spline native;
            uf.Curve.AskSplineData(spline.Tag, out native);
            if (native.knots == null || native.knots.Length == 0)
                throw new InvalidOperationException("NURBS knot data was unavailable for overlap analysis.");
            for (int i = 0; i < native.knots.Length; i++)
            {
                double knot = native.knots[i];
                if (!IsFinite(knot)) throw new InvalidOperationException("NURBS knot data contained a non-finite value.");
                if (knot > Math.Min(curve.StartParameter, curve.EndParameter) &&
                    knot < Math.Max(curve.StartParameter, curve.EndParameter)) breaks.Add(knot);
            }
        }
        breaks.Sort();
        double parameterRange = Math.Abs(curve.EndParameter - curve.StartParameter);
        double parameterTolerance = Math.Max(1.0e-13, parameterRange * 1.0e-12);
        List<double> uniqueBreaks = new List<double>();
        for (int i = 0; i < breaks.Count; i++)
            if (uniqueBreaks.Count == 0 || Math.Abs(uniqueBreaks[uniqueBreaks.Count - 1] - breaks[i]) > parameterTolerance)
                uniqueBreaks.Add(breaks[i]);
        if (uniqueBreaks.Count < 2) throw new InvalidOperationException("Native curve parameter range was degenerate.");

        int spans = uniqueBreaks.Count - 1;
        int subdivisions = spline == null ? 64 : SamplesPerNurbsSpan;
        if ((long)spans * subdivisions + 1 > MaximumOverlapSamples)
            throw new InvalidOperationException("Native overlap analysis exceeded its bounded parameter sample count.");
        List<double> parameters = new List<double>();
        for (int span = 0; span < spans; span++)
        {
            double start = uniqueBreaks[span];
            double end = uniqueBreaks[span + 1];
            for (int step = 0; step < subdivisions; step++)
                parameters.Add(start + (end - start) * step / subdivisions);
        }
        parameters.Add(uniqueBreaks[uniqueBreaks.Count - 1]);
        return parameters;
    }

    private static void AddDistinctMatch(List<CurveMatch> matches, CurveMatch candidate,
        CurveInfo source, CurveInfo target, double tolerance)
    {
        double sourceRange = Math.Abs(source.EndParameter - source.StartParameter);
        double targetRange = Math.Abs(target.EndParameter - target.StartParameter);
        double sourceTolerance = Math.Max(1.0e-13, sourceRange * 1.0e-12);
        double targetTolerance = Math.Max(1.0e-13, targetRange * 1.0e-12);
        for (int i = 0; i < matches.Count; i++)
            if (Math.Abs(matches[i].SourceParameter - candidate.SourceParameter) <= sourceTolerance &&
                Math.Abs(matches[i].TargetParameter - candidate.TargetParameter) <= targetTolerance)
                return;
        matches.Add(candidate);
    }

    private static bool HasContinuousMatch(UFSession uf, CurveInfo source, CurveInfo target,
        double tolerance, List<CurveMatch> matches)
    {
        matches.Sort(delegate(CurveMatch left, CurveMatch right)
        {
            return left.SourceParameter.CompareTo(right.SourceParameter);
        });
        for (int i = 0; i + 1 < matches.Count; i++)
        {
            CurveMatch start = matches[i];
            CurveMatch end = matches[i + 1];
            if (Distance(start.Point, end.Point) <= tolerance ||
                Math.Abs(end.SourceParameter - start.SourceParameter) <= 0.0 ||
                Math.Abs(end.TargetParameter - start.TargetParameter) <= 0.0)
                continue;
            bool continuous = true;
            for (int sample = 1; sample <= 3; sample++)
            {
                double fraction = sample / 4.0;
                double sourceParameter = start.SourceParameter + (end.SourceParameter - start.SourceParameter) * fraction;
                double[] sourcePoint = new double[3];
                uf.Curve.EvaluateCurve(source.Curve.Tag, sourceParameter, 0, sourcePoint);
                double targetParameter;
                double[] targetPoint = new double[3];
                uf.Eval.EvaluateClosestPoint(target.Evaluator, sourcePoint, out targetParameter, targetPoint);
                double targetLow = Math.Min(start.TargetParameter, end.TargetParameter);
                double targetHigh = Math.Max(start.TargetParameter, end.TargetParameter);
                double targetRange = Math.Abs(target.EndParameter - target.StartParameter);
                double targetMargin = Math.Max(1.0e-12, targetRange * 1.0e-9);
                double dx = sourcePoint[0] - targetPoint[0];
                double dy = sourcePoint[1] - targetPoint[1];
                double dz = sourcePoint[2] - targetPoint[2];
                if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > tolerance ||
                    targetParameter < targetLow - targetMargin || targetParameter > targetHigh + targetMargin)
                {
                    continuous = false;
                    break;
                }
                double[] sourceEval = new double[6];
                double[] targetEval = new double[6];
                uf.Curve.EvaluateCurve(source.Curve.Tag, sourceParameter, 1, sourceEval);
                uf.Curve.EvaluateCurve(target.Curve.Tag, targetParameter, 1, targetEval);
                Vector3d sourceTangent = new Vector3d(sourceEval[3], sourceEval[4], sourceEval[5]);
                Vector3d targetTangent = new Vector3d(targetEval[3], targetEval[4], targetEval[5]);
                double sourceMagnitude = Magnitude(sourceTangent);
                double targetMagnitude = Magnitude(targetTangent);
                if (sourceMagnitude <= 0.0 || targetMagnitude <= 0.0 ||
                    Magnitude(Cross(Scale(sourceTangent, 1.0 / sourceMagnitude),
                        Scale(targetTangent, 1.0 / targetMagnitude))) > DirectionTolerance)
                {
                    continuous = false;
                    break;
                }
            }
            if (continuous) return true;
        }
        return false;
    }

    private static bool TryClassifySections(Part part, UFSession uf, List<NXObject> curves,
        double tolerance, int expectedLoops, out int outerLoops, out int innerLoops,
        out int sectionLoops, out string note)
    {
        outerLoops = 0;
        innerLoops = 0;
        sectionLoops = 0;
        note = null;
        HashSet<Tag> before;
        HashSet<Tag> sectionTagsBefore;
        bool dirtyBefore;
        try
        {
            before = SnapshotPartObjects(part, uf);
            sectionTagsBefore = SnapshotSectionTags(part);
            dirtyBefore = part.IsModified;
        }
        catch
        {
            note = "section_inventory_before_unavailable";
            return false;
        }

        Session session = Session.GetSession();
        Session.UndoMarkId mark = default(Session.UndoMarkId);
        bool markSet = false;
        bool undone = false;
        Exception operationFailure = null;
        Exception cleanupFailure = null;
        Section[] sections = null;
        int loops = 0;
        int outer = 0;
        try
        {
            mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F06 profile section classification");
            markSet = true;
            part.Sections.CreateSectionsUsingCurves(curves.ToArray(),
                SectionCollection.LoopOption.SeparateOuter, tolerance, tolerance, 0.5, out sections);
            if (sections == null) sections = new Section[0];
            outer = sections.Length;
            for (int i = 0; i < sections.Length; i++)
            {
                NXObject[] output;
                sections[i].EvaluateAndAskOutputEntities(out output);
                loops += sections[i].GetNumberOfLoops();
            }
        }
        catch (Exception ex)
        {
            operationFailure = ex;
        }
        finally
        {
            if (markSet)
            {
                try
                {
                    Section[] currentSections = part.Sections.ToArray();
                    for (int i = 0; i < currentSections.Length; i++)
                    {
                        if (sectionTagsBefore.Contains(currentSections[i].Tag)) continue;
                        currentSections[i].Destroy();
                    }
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                try
                {
                    session.UndoToMark(mark, null);
                    undone = true;
                }
                catch (Exception ex)
                {
                    if (cleanupFailure == null) cleanupFailure = ex;
                }
                if (undone)
                {
                    try { session.DeleteUndoMark(mark, null); }
                    catch (Exception ex) { if (cleanupFailure == null) cleanupFailure = ex; }
                }
            }
        }

        if (!markSet)
        {
            note = "section_classification_failed";
            return false;
        }
        if (cleanupFailure != null || !undone)
            throw new SectionCleanupException("F06 temporary Section cleanup/undo failed; model restoration is not verified.", cleanupFailure ?? operationFailure);
        try
        {
            HashSet<Tag> after = SnapshotPartObjects(part, uf);
            bool dirtyAfter = part.IsModified;
            if (dirtyAfter != dirtyBefore || !SameTags(before, after))
                throw new SectionCleanupException("F06 Section analysis did not restore part inventory and modified state.", operationFailure);
        }
        catch (SectionCleanupException) { throw; }
        catch (Exception ex)
        {
            throw new SectionCleanupException("F06 Section cleanup could not verify restored part inventory.", ex);
        }

        if (operationFailure != null)
        {
            note = "section_classification_failed";
            return false;
        }
        if (loops != expectedLoops || outer < 0 || outer > loops)
        {
            note = "section_loop_counts_disagree_with_endpoint_graph";
            return false;
        }
        outerLoops = outer;
        sectionLoops = loops;
        innerLoops = loops - outer;
        return true;
    }

    private static HashSet<Tag> SnapshotSectionTags(Part part)
    {
        HashSet<Tag> result = new HashSet<Tag>();
        Section[] sections = part.Sections.ToArray();
        for (int i = 0; i < sections.Length; i++) result.Add(sections[i].Tag);
        return result;
    }

    private static HashSet<Tag> SnapshotPartObjects(Part part, UFSession uf)
    {
        HashSet<Tag> result = new HashSet<Tag>();
        Tag current = Tag.Null;
        int count = 0;
        while (true)
        {
            current = uf.Obj.CycleAll(part.Tag, current);
            if (current == Tag.Null) break;
            result.Add(current);
            count++;
            if (count > 500000) throw new InvalidOperationException("Part object inventory exceeded the safety bound.");
        }
        return result;
    }

    private static bool SameTags(HashSet<Tag> first, HashSet<Tag> second)
    {
        if (first.Count != second.Count) return false;
        foreach (Tag tag in first) if (!second.Contains(tag)) return false;
        return true;
    }

    private static double Distance(Point3d a, Point3d b)
    {
        double x = a.X - b.X;
        double y = a.Y - b.Y;
        double z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static Vector3d Subtract(Point3d a, Point3d b)
    {
        return new Vector3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }

    private static Vector3d Scale(Vector3d v, double scale)
    {
        return new Vector3d(v.X * scale, v.Y * scale, v.Z * scale);
    }

    private static Vector3d Add(Vector3d first, Vector3d second)
    {
        return new Vector3d(first.X + second.X, first.Y + second.Y, first.Z + second.Z);
    }

    private static double DistanceVector(Vector3d first, Vector3d second)
    {
        double x = first.X - second.X;
        double y = first.Y - second.Y;
        double z = first.Z - second.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static double Magnitude(Vector3d v)
    {
        return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }

    private static double Dot(Vector3d a, Vector3d b)
    {
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    private static Vector3d Cross(Vector3d a, Vector3d b)
    {
        return new Vector3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    private static bool IsFinite(double value)
    {
        return !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
}
