// Read-only N04 surface-divergence volume diagnostic. This is supplemental
// geometry evidence; it never replaces the existing NX mass-property gate.
// Compile with the F13 NXOpen/UF harness. C# 5 compatible.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NXOpen;
using NXOpen.UF;

public static class F13SurfaceVolumeDiagnostic
{
    private const string EvidenceSchema = "f13-surface-divergence-v1";
    private const int UfModlPeriodic = 1;
    private const int UfModlToroidalTopology = 5;
    private const int UfPointInside = 1;
    private const int UfPointOutside = 2;
    private const int UfPointOn = 3;
    private const double SeamTangentRelativeTolerance = 1.0e-7;
    private const double PeriodRelativeTolerance = 1.0e-10;

    private sealed class GridResult
    {
        public int N;
        public double Offset;
        public double[] Volumes = new double[2];
        public Dictionary<string, object> Summary;
        public Dictionary<string, object> Evidence;
    }

    /// <summary>
    /// Captures a direct UF surface-divergence diagnostic for an already
    /// measured N04 candidate. The caller owns unique-path selection and the
    /// existing mass-property acceptance gate.
    /// </summary>
    public static Dictionary<string, object> Capture(Body body, string runId,
        string evidencePath, double expectedVolumeMm3, double existingToleranceMm3)
    {
        Dictionary<string, object> summary = Map();
        Dictionary<string, object> evidence = Map();
        List<object> failures = new List<object>();
        List<object> criteria = new List<object>();
        List<object> gridSummaries = new List<object>();
        List<object> gridEvidence = new List<object>();
        summary["schema"] = EvidenceSchema;
        summary["diagnostic"] = "N04 direct periodic-face divergence volume diagnostic";
        summary["status"] = "FAILED";
        summary["applicability_status"] = "UNKNOWN";
        summary["diagnostic_only"] = true;
        summary["mass_property_gate_replaced"] = false;
        summary["finite_resolution_is_rigorous_error_bound"] = false;
        summary["run_id"] = runId;
        summary["evidence_path"] = evidencePath;
        summary["evidence_write_status"] = "PENDING";
        summary["units"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "part_length", "mm" }, { "face_position", "mm" },
            { "surface_derivatives", "mm/native-parameter" },
            { "uv_parameters_and_periods", "native parameter values as returned by UF" },
            { "volume", "mm^3" }
        };
        summary["criteria"] = criteria;
        summary["failure_reasons"] = failures;
        summary["grids"] = gridSummaries;
        summary["raw_samples_in_summary"] = false;
        summary["raw_samples_evidence_key"] = "grids[].samples";
        evidence["schema"] = EvidenceSchema;
        evidence["run_id"] = runId;
        evidence["body"] = null;
        evidence["summary"] = summary;
        evidence["grids"] = gridEvidence;

        bool eligible = false;
        bool diagnosticPass = false;
        try
        {
            Require(body != null, "Candidate body argument is null.");
            Require(!String.IsNullOrEmpty(evidencePath), "Evidence path is null or empty.");
            Require(IsFinite(expectedVolumeMm3) && expectedVolumeMm3 > 0.0,
                "Analytic expected volume must be finite and positive.");
            Require(IsFinite(existingToleranceMm3) && existingToleranceMm3 > 0.0,
                "Existing N04 volume tolerance must be finite and positive.");

            summary["expected_volume_mm3"] = expectedVolumeMm3;
            summary["expected_volume_source"] =
                "Caller-supplied existing N04 analytic expectation; the separate main report retains its formula and fixture definition.";
            summary["existing_acceptance_tolerance_mm3"] = existingToleranceMm3;
            summary["refinement_and_origin_tolerance_mm3"] = existingToleranceMm3 / 10.0;
            summary["candidate_measurement_precondition"] =
                "Caller invokes this read-only diagnostic only after the existing N04 candidate mass-property measurement.";
            summary["candidate_measurement_performed_here"] = false;

            BasePart owningPart = body.OwningPart;
            bool partIsMillimeters = owningPart != null && owningPart.PartUnits == BasePart.Units.Millimeters;
            summary["part_units"] = owningPart == null ? "UNKNOWN" : owningPart.PartUnits.ToString();
            Check(criteria, failures, "part_units_are_millimeters", partIsMillimeters,
                summary["part_units"], "Millimeters");

            Dictionary<string, object> bodyEvidence = Map();
            evidence["body"] = bodyEvidence;
            bodyEvidence["tag"] = body.Tag.ToString();
            bodyEvidence["journal_identifier"] = body.JournalIdentifier;
            bodyEvidence["is_solid"] = body.IsSolidBody;
            bodyEvidence["part_tag"] = owningPart == null ? null : owningPart.Tag.ToString();
            summary["candidate_body_count_in_capture"] = 1;
            summary["single_solid_output_count_precondition"] =
                "The calling N04 harness must directly verify exactly one output body and that it is solid before invoking Capture.";
            Check(criteria, failures, "candidate_body_is_solid", body.IsSolidBody,
                body.IsSolidBody, true);

            Face[] faces = body.GetFaces();
            Edge[] edges = body.GetEdges();
            int faceCount = faces == null ? -1 : faces.Length;
            int edgeCount = edges == null ? -1 : edges.Length;
            bodyEvidence["face_count"] = faceCount;
            bodyEvidence["edge_count"] = edgeCount;
            Check(criteria, failures, "exactly_one_face", faceCount == 1, faceCount, 1);
            Check(criteria, failures, "zero_edges", edgeCount == 0, edgeCount, 0);

            UFModl modl = UFSession.GetUFSession().Modl;
            int consistencyFaultCount;
            int[] consistencyFaultTypes;
            Tag[] consistencyFaultTags;
            modl.AskBodyConsistency(body.Tag, out consistencyFaultCount,
                out consistencyFaultTypes, out consistencyFaultTags);
            bodyEvidence["consistency_fault_count"] = consistencyFaultCount;
            bodyEvidence["consistency_fault_types"] = consistencyFaultTypes ?? new int[0];
            bodyEvidence["consistency_fault_tags"] = TagStrings(consistencyFaultTags);
            Check(criteria, failures, "uf_body_consistency_zero", consistencyFaultCount == 0,
                consistencyFaultCount, 0);

            double[] exactBoxCorner = new double[3];
            double[,] exactBoxAxes = new double[3, 3];
            double[] exactBoxLengths = new double[3];
            modl.AskBoundingBoxExact(body.Tag, Tag.Null, exactBoxCorner, exactBoxAxes, exactBoxLengths);
            bodyEvidence["exact_box_corner_mm"] = exactBoxCorner;
            bodyEvidence["exact_box_axes_rows"] = MatrixRows(exactBoxAxes);
            bodyEvidence["exact_box_lengths_mm"] = exactBoxLengths;
            double[] boxMin;
            double[] boxMax;
            ComputeWorldBounds(exactBoxCorner, exactBoxAxes, exactBoxLengths, out boxMin, out boxMax);
            bodyEvidence["world_aabb_min_mm"] = boxMin;
            bodyEvidence["world_aabb_max_mm"] = boxMax;
            double[] boxRange = new double[3];
            double boxDiagonalSquared = 0.0;
            double minimumPositiveSpan = Double.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                boxRange[axis] = boxMax[axis] - boxMin[axis];
                boxDiagonalSquared += boxRange[axis] * boxRange[axis];
                if (boxRange[axis] > 0.0 && boxRange[axis] < minimumPositiveSpan)
                    minimumPositiveSpan = boxRange[axis];
            }
            double boxDiagonal = Math.Sqrt(boxDiagonalSquared);
            double[] originA = new double[]
            {
                (boxMin[0] + boxMax[0]) * 0.5,
                (boxMin[1] + boxMax[1]) * 0.5,
                (boxMin[2] + boxMax[2]) * 0.5
            };
            double moderateShift = boxDiagonal / Math.Sqrt(3.0);
            double[] originB = new double[]
            {
                originA[0] + 0.125 * moderateShift,
                originA[1] - 0.0625 * moderateShift,
                originA[2] + 0.03125 * moderateShift
            };
            double[][] origins = new double[][] { originA, originB };
            bodyEvidence["world_aabb_ranges_mm"] = boxRange;
            bodyEvidence["coordinate_origins_mm"] = origins;
            Check(criteria, failures, "two_distinct_moderate_origins",
                Distance(originA, originB) > 0.0 && Distance(originA, originB) < boxDiagonal,
                Distance(originA, originB), "strictly between zero and AABB diagonal");

            double partDistanceToleranceMm;
            modl.AskDistanceToleranceOfPart(body.Tag, out partDistanceToleranceMm);
            summary["part_distance_tolerance_mm"] = partDistanceToleranceMm;
            Check(criteria, failures, "finite_positive_part_distance_tolerance",
                IsFinite(partDistanceToleranceMm) && partDistanceToleranceMm > 0.0,
                partDistanceToleranceMm, "finite and positive");
            double positionClosureToleranceMm = Math.Max(1.0e-9, boxDiagonal * 1.0e-9);
            double witnessOffsetMm = Math.Max(partDistanceToleranceMm * 10.0, boxDiagonal * 1.0e-7);
            summary["seam_position_closure_tolerance_mm"] = positionClosureToleranceMm;
            summary["seam_derivative_relative_closure_tolerance"] = SeamTangentRelativeTolerance;
            summary["uv_span_period_relative_tolerance"] = PeriodRelativeTolerance;
            summary["containment_witness_offset_mm"] = witnessOffsetMm;
            bool witnessOffsetSmall = IsFinite(witnessOffsetMm) && minimumPositiveSpan > 0.0 &&
                witnessOffsetMm < minimumPositiveSpan * 0.01;
            summary["containment_witness_offset_is_small_relative_to_body"] = witnessOffsetSmall;
            Check(criteria, failures, "containment_witness_offset_away_from_tolerance_and_small",
                IsFinite(partDistanceToleranceMm) && witnessOffsetMm >= partDistanceToleranceMm * 10.0 && witnessOffsetSmall,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "offset_mm", witnessOffsetMm }, { "part_distance_tolerance_mm", partDistanceToleranceMm },
                    { "minimum_positive_aabb_span_mm", minimumPositiveSpan },
                    { "maximum_fraction_of_minimum_span", 0.01 }
                }, "offset >= 10x part distance tolerance and < 1% of minimum positive AABB span");

            Face face = faceCount == 1 ? faces[0] : null;
            Dictionary<string, object> faceEvidence = Map();
            bodyEvidence["face"] = faceEvidence;
            bool topologyPass = false;
            bool periodicityPass = false;
            bool spanPass = false;
            bool seamPass = false;
            bool orientationPass = false;
            double[] uv = null;
            int topology = 0;
            int statusU = Int32.MinValue;
            int statusV = Int32.MinValue;
            double periodU = Double.NaN;
            double periodV = Double.NaN;
            int nativeFaceType = Int32.MinValue;
            int normalDirection = 0;
            double[] faceDataPoint = new double[3];
            double[] faceDataDirection = new double[3];
            double[] faceDataBox = new double[6];
            double radius;
            double radiusData;
            if (face != null)
            {
                faceEvidence["tag"] = face.Tag.ToString();
                faceEvidence["journal_identifier"] = face.JournalIdentifier;
                modl.AskFaceTopology(face.Tag, out topology);
                faceEvidence["uf_face_topology"] = topology;
                topologyPass = topology == UfModlToroidalTopology;
                Check(criteria, failures, "face_topology_toroidal_no_peripheral_loops",
                    topologyPass, topology, UfModlToroidalTopology);

                modl.AskFacePeriodicity(face.Tag, out statusU, out periodU, out statusV, out periodV);
                faceEvidence["u_periodicity_status"] = statusU;
                faceEvidence["u_period_native"] = periodU;
                faceEvidence["v_periodicity_status"] = statusV;
                faceEvidence["v_period_native"] = periodV;
                periodicityPass = statusU == UfModlPeriodic && statusV == UfModlPeriodic &&
                    IsFinite(periodU) && periodU > 0.0 && IsFinite(periodV) && periodV > 0.0;
                Check(criteria, failures, "both_uv_directions_periodic",
                    periodicityPass,
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "u_status", statusU }, { "u_period_native", periodU },
                        { "v_status", statusV }, { "v_period_native", periodV }
                    }, "U status=1 and V status=1 with finite positive periods");

                uv = new double[4];
                modl.AskFaceUvMinmax(face.Tag, uv);
                faceEvidence["uv_limits_native"] = uv;
                double spanU = uv[1] - uv[0];
                double spanV = uv[3] - uv[2];
                double spanToleranceU = Math.Max(1.0e-12, Math.Abs(periodU) * PeriodRelativeTolerance);
                double spanToleranceV = Math.Max(1.0e-12, Math.Abs(periodV) * PeriodRelativeTolerance);
                bool spanUOnePeriod = IsFinite(spanU) && IsFinite(periodU) &&
                    Math.Abs(spanU - periodU) <= spanToleranceU;
                bool spanVOnePeriod = IsFinite(spanV) && IsFinite(periodV) &&
                    Math.Abs(spanV - periodV) <= spanToleranceV;
                faceEvidence["uv_span_native"] = new double[] { spanU, spanV };
                faceEvidence["uv_span_minus_period_native"] = new double[] { spanU - periodU, spanV - periodV };
                faceEvidence["uv_span_period_tolerance_native"] = new double[] { spanToleranceU, spanToleranceV };
                faceEvidence["u_span_is_exactly_one_period"] = spanUOnePeriod;
                faceEvidence["v_span_is_exactly_one_period"] = spanVOnePeriod;
                spanPass = spanUOnePeriod && spanVOnePeriod;
                Check(criteria, failures, "uv_span_is_one_period_in_each_direction",
                    spanPass,
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "span_native", new double[] { spanU, spanV } },
                        { "period_native", new double[] { periodU, periodV } },
                        { "absolute_residual_native", new double[] { Math.Abs(spanU - periodU), Math.Abs(spanV - periodV) } },
                        { "tolerance_native", new double[] { spanToleranceU, spanToleranceV } }
                    }, "both span-period residuals are within the recorded relative parameter tolerance");

                modl.AskFaceData(face.Tag, out nativeFaceType, faceDataPoint, faceDataDirection,
                    faceDataBox, out radius, out radiusData, out normalDirection);
                faceEvidence["native_face_type"] = nativeFaceType;
                faceEvidence["face_data_point_mm"] = faceDataPoint;
                faceEvidence["face_data_direction"] = faceDataDirection;
                faceEvidence["face_data_box_mm"] = faceDataBox;
                faceEvidence["radius_mm"] = radius;
                faceEvidence["radius_data"] = radiusData;
                faceEvidence["norm_dir"] = normalDirection;
                faceEvidence["norm_dir_interpretation"] = "+1 means the oriented face normal agrees with the surface normal cross(du,dv); -1 means reversed, per UF_MODL_ask_face_data.";

                if (periodicityPass && spanPass && IsFinite(uv[0]) && IsFinite(uv[1]) &&
                    IsFinite(uv[2]) && IsFinite(uv[3]))
                {
                    Dictionary<string, object> seamEvidence;
                    seamPass = CheckSeamClosure(modl, face.Tag, uv, boxDiagonal,
                        positionClosureToleranceMm, SeamTangentRelativeTolerance,
                        out seamEvidence);
                    faceEvidence["seam_closure"] = seamEvidence;
                    Check(criteria, failures, "periodic_seam_position_and_first_tangent_closure",
                        seamPass, seamEvidence["maximum_position_residual_mm"],
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "position_tolerance_mm", positionClosureToleranceMm },
                            { "both_first_derivative_relative_residuals_at_each_seam", SeamTangentRelativeTolerance },
                            { "samples_per_seam", 4 }
                        });
                }
                else
                {
                    faceEvidence["seam_closure"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "status", "NOT_EVALUATED_PERIODIC_DOMAIN_GATE_FAILED" }
                    };
                    AddFailure(failures, "Periodic seam closure was not evaluated because the full periodic domain gate failed.");
                }

                if (normalDirection == 1 || normalDirection == -1)
                {
                    Dictionary<string, object> orientationEvidence;
                    orientationPass = CheckOrientationAndWitnesses(modl, face.Tag, body.Tag,
                        uv, normalDirection, witnessOffsetMm, witnessOffsetSmall,
                        out orientationEvidence);
                    faceEvidence["orientation"] = orientationEvidence;
                    Check(criteria, failures, "native_normal_cross_product_sense_and_containment_witnesses",
                        orientationPass, orientationEvidence,
                        "norm_dir is +/-1, native normal is parallel/antiparallel to cross(du,dv), and +/- outward witnesses classify outside/inside");
                }
                else
                {
                    faceEvidence["orientation"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "status", "FAILED_INVALID_NORM_DIR" }, { "norm_dir", normalDirection }
                    };
                    AddFailure(failures, "UF AskFaceData norm_dir was not +1 or -1.");
                }
            }
            else
            {
                Check(criteria, failures, "face_topology_toroidal_no_peripheral_loops", false,
                    "No unique face was available.", UfModlToroidalTopology);
                Check(criteria, failures, "both_uv_directions_periodic", false,
                    "No unique face was available.", "U and V periodic");
                Check(criteria, failures, "uv_span_is_one_period_in_each_direction", false,
                    "No unique face was available.", "one period in U and V");
                AddFailure(failures, "Unique-face topology, periodicity, seam, and orientation evidence is unavailable.");
            }

            bool baseTopologyPass = body.IsSolidBody && partIsMillimeters && faceCount == 1 &&
                edgeCount == 0 && consistencyFaultCount == 0 && topologyPass &&
                periodicityPass && spanPass && seamPass && orientationPass &&
                IsFinite(partDistanceToleranceMm) && partDistanceToleranceMm > 0.0 &&
                witnessOffsetSmall && Distance(originA, originB) > 0.0 && Distance(originA, originB) < boxDiagonal;
            eligible = baseTopologyPass;
            summary["applicability_status"] = eligible ? "ELIGIBLE" : "NOT_APPLICABLE";
            summary["applicability_criteria_passed"] = eligible;
            if (!eligible)
                AddFailure(failures, "N04 divergence applicability was not established by the required direct topology, seam, and orientation evidence.");

            if (eligible)
            {
                int outwardSign = Convert.ToInt32(((Dictionary<string, object>)faceEvidence["orientation"])["outward_sign_for_cross_du_dv"], CultureInfo.InvariantCulture);
                GridResult[] results = new GridResult[6];
                int resultIndex = 0;
                int[] levels = new int[] { 32, 64, 128 };
                double[] offsets = new double[] { 0.25, 0.75 };
                for (int levelIndex = 0; levelIndex < levels.Length; levelIndex++)
                {
                    for (int offsetIndex = 0; offsetIndex < offsets.Length; offsetIndex++)
                    {
                        GridResult grid = IntegrateGrid(modl, face.Tag, uv, levels[levelIndex],
                            offsets[offsetIndex], outwardSign, origins, resultIndex);
                        results[resultIndex++] = grid;
                        gridSummaries.Add(grid.Summary);
                        gridEvidence.Add(grid.Evidence);
                    }
                }
                bool convergencePass = EvaluateComparisons(results, existingToleranceMm3,
                    summary, failures);
                double finalVolume = 0.0;
                int finalStart = 4;
                for (int index = finalStart; index < results.Length; index++)
                    finalVolume += (results[index].Volumes[0] + results[index].Volumes[1]) * 0.25;
                double signedError = finalVolume - expectedVolumeMm3;
                bool analyticPass = IsFinite(finalVolume) && Math.Abs(signedError) <= existingToleranceMm3;
                summary["final_signed_volume_mm3"] = finalVolume;
                summary["analytic_expectation_comparison"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "analytic_expected_volume_mm3", expectedVolumeMm3 },
                    { "final_signed_surface_volume_mm3", finalVolume },
                    { "signed_error_mm3", signedError },
                    { "absolute_error_mm3", Math.Abs(signedError) },
                    { "original_n04_tolerance_mm3", existingToleranceMm3 },
                    { "passed", analyticPass },
                    { "comparison", "abs(final signed diagnostic volume - analytic expectation) <= unchanged original N04 tolerance" }
                };
                Check(criteria, failures, "final_signed_volume_within_original_analytic_gate",
                    analyticPass, summary["analytic_expectation_comparison"],
                    "absolute final estimate error <= existing N04 tolerance");
                diagnosticPass = convergencePass && analyticPass;
            }

            summary["status"] = eligible && diagnosticPass && failures.Count == 0 ? "PASS" : "FAILED";
            if (!eligible)
                summary["status"] = "FAILED";
        }
        catch (Exception ex)
        {
            summary["status"] = "FAILED";
            summary["applicability_status"] = eligible ? "ELIGIBLE_DIAGNOSTIC_FAILED" : "UNKNOWN";
            summary["exception"] = ex.ToString();
            AddFailure(failures, ex.GetType().FullName + ": " + ex.Message);
        }

        try
        {
            summary["evidence_write_status"] = "PENDING";
            WriteEvidence(evidencePath, evidence);
            summary["evidence_write_status"] = "WRITTEN";
            WriteEvidence(evidencePath, evidence);
        }
        catch (Exception writeException)
        {
            summary["status"] = "FAILED";
            summary["evidence_write_status"] = "FAILED";
            summary["evidence_write_exception"] = writeException.ToString();
            AddFailure(failures, "Evidence JSON could not be written: " + writeException.Message);
            try { WriteEvidence(evidencePath, evidence); }
            catch { }
        }
        return summary;
    }

    private static bool CheckSeamClosure(UFModl modl, Tag face, double[] uv,
        double scaleMm, double positionToleranceMm, double derivativeRelativeTolerance,
        out Dictionary<string, object> evidence)
    {
        evidence = Map();
        List<object> seamSamples = new List<object>();
        evidence["samples"] = seamSamples;
        evidence["status"] = "CHECKED";
        double[] fractions = new double[] { 0.125, 0.375, 0.625, 0.875 };
        double maximumPositionResidual = 0.0;
        double maximumDuRelativeResidual = 0.0;
        double maximumDvRelativeResidual = 0.0;
        bool passed = true;
        for (int index = 0; index < fractions.Length; index++)
        {
            double fraction = fractions[index];
            double v = uv[2] + fraction * (uv[3] - uv[2]);
            Dictionary<string, object> uSample = CompareSeamPair(modl, face,
                new double[] { uv[0], v }, new double[] { uv[1], v }, "u_seam", fraction,
                positionToleranceMm, derivativeRelativeTolerance);
            seamSamples.Add(uSample);
            maximumPositionResidual = Math.Max(maximumPositionResidual,
                Convert.ToDouble(uSample["position_residual_mm"], CultureInfo.InvariantCulture));
            maximumDuRelativeResidual = Math.Max(maximumDuRelativeResidual,
                Convert.ToDouble(uSample["du_relative_residual"], CultureInfo.InvariantCulture));
            maximumDvRelativeResidual = Math.Max(maximumDvRelativeResidual,
                Convert.ToDouble(uSample["dv_relative_residual"], CultureInfo.InvariantCulture));
            passed = passed && (bool)uSample["passed"];

            double u = uv[0] + fraction * (uv[1] - uv[0]);
            Dictionary<string, object> vSample = CompareSeamPair(modl, face,
                new double[] { u, uv[2] }, new double[] { u, uv[3] }, "v_seam", fraction,
                positionToleranceMm, derivativeRelativeTolerance);
            seamSamples.Add(vSample);
            maximumPositionResidual = Math.Max(maximumPositionResidual,
                Convert.ToDouble(vSample["position_residual_mm"], CultureInfo.InvariantCulture));
            maximumDuRelativeResidual = Math.Max(maximumDuRelativeResidual,
                Convert.ToDouble(vSample["du_relative_residual"], CultureInfo.InvariantCulture));
            maximumDvRelativeResidual = Math.Max(maximumDvRelativeResidual,
                Convert.ToDouble(vSample["dv_relative_residual"], CultureInfo.InvariantCulture));
            passed = passed && (bool)vSample["passed"];
        }
        evidence["maximum_position_residual_mm"] = maximumPositionResidual;
        evidence["position_tolerance_mm"] = positionToleranceMm;
        evidence["maximum_du_relative_residual"] = maximumDuRelativeResidual;
        evidence["maximum_dv_relative_residual"] = maximumDvRelativeResidual;
        evidence["derivative_relative_tolerance"] = derivativeRelativeTolerance;
        evidence["passed"] = passed;
        evidence["scale_context_mm"] = scaleMm;
        return passed;
    }

    private static Dictionary<string, object> CompareSeamPair(UFModl modl, Tag face,
        double[] lowerUv, double[] upperUv, string seam, double fraction,
        double positionToleranceMm, double derivativeRelativeTolerance)
    {
        Dictionary<string, object> record = Map();
        FaceProps lower = ReadFaceProps(modl, face, lowerUv);
        FaceProps upper = ReadFaceProps(modl, face, upperUv);
        double positionResidual = Distance(lower.Point, upper.Point);
        double duResidual = RelativeVectorDifference(lower.Du, upper.Du);
        double dvResidual = RelativeVectorDifference(lower.Dv, upper.Dv);
        bool passed = positionResidual <= positionToleranceMm &&
            duResidual <= derivativeRelativeTolerance && dvResidual <= derivativeRelativeTolerance;
        record["seam"] = seam;
        record["fraction_along_other_parameter"] = fraction;
        record["lower_uv_native"] = lowerUv;
        record["upper_uv_native"] = upperUv;
        record["lower_point_mm"] = lower.Point;
        record["upper_point_mm"] = upper.Point;
        record["position_residual_mm"] = positionResidual;
        record["position_tolerance_mm"] = positionToleranceMm;
        record["lower_du_mm_per_native_u"] = lower.Du;
        record["upper_du_mm_per_native_u"] = upper.Du;
        record["du_relative_residual"] = duResidual;
        record["lower_dv_mm_per_native_v"] = lower.Dv;
        record["upper_dv_mm_per_native_v"] = upper.Dv;
        record["dv_relative_residual"] = dvResidual;
        record["derivative_relative_tolerance"] = derivativeRelativeTolerance;
        record["passed"] = passed;
        return record;
    }

    private static bool CheckOrientationAndWitnesses(UFModl modl, Tag face, Tag body,
        double[] uv, int normalDirection, double witnessOffsetMm,
        bool witnessOffsetSmall, out Dictionary<string, object> evidence)
    {
        evidence = Map();
        double[] centerUv = new double[] { (uv[0] + uv[1]) * 0.5, (uv[2] + uv[3]) * 0.5 };
        FaceProps center = ReadFaceProps(modl, face, centerUv);
        double[] cross = Cross(center.Du, center.Dv);
        double crossLength = Norm(cross);
        double normalLength = Norm(center.Normal);
        Require(crossLength > 0.0 && IsFinite(crossLength), "Center cross(du,dv) is degenerate or non-finite.");
        Require(normalLength > 0.0 && IsFinite(normalLength), "AskFaceProps returned a degenerate or non-finite native normal.");
        double[] unitCross = Scale(cross, 1.0 / crossLength);
        double[] unitNativeNormal = Scale(center.Normal, 1.0 / normalLength);
        double normalCrossDot = Dot(unitNativeNormal, unitCross);
        double[] outwardNormal = Scale(unitCross, normalDirection);
        int crossSense = normalCrossDot >= 0.0 ? 1 : -1;
        // UF_MODL_ask_face_data norm_dir is the authoritative face-orientation
        // sign relative to the surface normal cross(du,dv). AskFaceProps' normal
        // is retained and checked for parallelism, but its sign is not multiplied
        // a second time into the oriented surface integral.
        int outwardSign = normalDirection;
        double angularResidual = 1.0 - Math.Abs(normalCrossDot);
        bool normalsAligned = angularResidual <= 1.0e-8;
        evidence["status"] = "CHECKED";
        evidence["center_uv_native"] = centerUv;
        evidence["center_point_mm"] = center.Point;
        evidence["center_du_mm_per_native_u"] = center.Du;
        evidence["center_dv_mm_per_native_v"] = center.Dv;
        evidence["cross_du_dv"] = cross;
        evidence["native_face_normal"] = center.Normal;
        evidence["unit_cross_du_dv"] = unitCross;
        evidence["unit_native_face_normal"] = unitNativeNormal;
        evidence["native_normal_dot_unit_cross"] = normalCrossDot;
        evidence["normal_alignment_residual_1_minus_abs_dot"] = angularResidual;
        evidence["normal_alignment_tolerance"] = 1.0e-8;
        evidence["norm_dir"] = normalDirection;
        evidence["cross_sense_relative_to_native_normal"] = crossSense;
        evidence["outward_normal_from_norm_dir"] = outwardNormal;
        evidence["outward_sign_for_cross_du_dv"] = outwardSign;
        evidence["normal_alignment_passed"] = normalsAligned;
        List<object> witnesses = new List<object>();
        evidence["containment_witnesses"] = witnesses;
        bool witnessPass = false;
        if (witnessOffsetSmall)
        {
            double[] plusPoint = Add(center.Point, Scale(outwardNormal, witnessOffsetMm));
            double[] minusPoint = Add(center.Point, Scale(outwardNormal, -witnessOffsetMm));
            int plusActual;
            int minusActual;
            modl.AskPointContainment(plusPoint, body, out plusActual);
            modl.AskPointContainment(minusPoint, body, out minusActual);
            witnesses.Add(ContainmentRecord("plus_outward_normal", plusPoint,
                plusActual, UfPointOutside, witnessOffsetMm));
            witnesses.Add(ContainmentRecord("minus_outward_normal", minusPoint,
                minusActual, UfPointInside, witnessOffsetMm));
            witnessPass = plusActual == UfPointOutside && minusActual == UfPointInside;
            evidence["witness_offset_mm"] = witnessOffsetMm;
            evidence["witness_offset_at_least_10x_distance_tolerance"] = true;
            evidence["witness_classifications_consistent"] = witnessPass;
            evidence["witness_actual_codes"] = new int[] { plusActual, minusActual };
        }
        else
        {
            evidence["witness_offset_mm"] = witnessOffsetMm;
            evidence["witness_offset_at_least_10x_distance_tolerance"] = false;
            evidence["witness_classifications_consistent"] = false;
            evidence["witness_status"] = "NOT_RUN_OFFSET_NOT_SMALL_RELATIVE_TO_BODY";
        }
        bool passed = normalsAligned && witnessPass && (normalDirection == 1 || normalDirection == -1);
        evidence["passed"] = passed;
        return passed;
    }

    private static Dictionary<string, object> ContainmentRecord(string label,
        double[] point, int actual, int expected, double offsetMm)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "label", label }, { "point_mm", point }, { "offset_mm", offsetMm },
            { "actual_code", actual }, { "expected_code", expected },
            { "classification", PointClass(actual) }, { "passed", actual == expected }
        };
    }

    private static string PointClass(int code)
    {
        if (code == UfPointInside) return "inside";
        if (code == UfPointOutside) return "outside";
        if (code == UfPointOn) return "on";
        return "unknown_code";
    }

    private static GridResult IntegrateGrid(UFModl modl, Tag face, double[] uv,
        int n, double offset, int outwardSign, double[][] origins, int index)
    {
        GridResult result = new GridResult();
        result.N = n;
        result.Offset = offset;
        double spanU = uv[1] - uv[0];
        double spanV = uv[3] - uv[2];
        double weightU = spanU / n;
        double weightV = spanV / n;
        double sampleWeight = weightU * weightV;
        double[] sums = new double[2];
        double[] corrections = new double[2];
        List<object> rawSamples = new List<object>(n * n);
        Dictionary<string, object> record = Map();
        record["grid_index"] = index;
        record["n"] = n;
        record["offset_fraction"] = offset;
        record["sample_order"] = "i_u_outer_j_v_inner";
        record["uv_limits_native"] = (double[])uv.Clone();
        record["integration_weights_native_uv"] = new double[] { weightU, weightV };
        record["sample_weight_native_uv"] = sampleWeight;
        record["sample_count"] = n * n;
        record["raw_sample_layout"] = new string[]
        {
            "u", "v", "px_mm", "py_mm", "pz_mm", "dux_mm_per_native_u", "duy_mm_per_native_u",
            "duz_mm_per_native_u", "dvx_mm_per_native_v", "dvy_mm_per_native_v", "dvz_mm_per_native_v",
            "nx", "ny", "nz"
        };
        record["origins"] = new List<object>();
        record["samples"] = rawSamples;
        result.Evidence = record;

        for (int i = 0; i < n; i++)
        {
            double u = uv[0] + (i + offset) * spanU / n;
            for (int j = 0; j < n; j++)
            {
                double v = uv[2] + (j + offset) * spanV / n;
                FaceProps props = ReadFaceProps(modl, face, new double[] { u, v });
                double[] raw = new double[14];
                raw[0] = u; raw[1] = v;
                Copy3(props.Point, raw, 2); Copy3(props.Du, raw, 5);
                Copy3(props.Dv, raw, 8); Copy3(props.Normal, raw, 11);
                EnsureFinite(raw, "Raw periodic surface sample contains a non-finite value.");
                rawSamples.Add(raw);
                double[] cross = Cross(props.Du, props.Dv);
                for (int originIndex = 0; originIndex < 2; originIndex++)
                {
                    double[] offsetPoint = Subtract(props.Point, origins[originIndex]);
                    double weightedFlux = Dot(offsetPoint, cross) * sampleWeight;
                    AddCompensated(weightedFlux, ref sums[originIndex], ref corrections[originIndex]);
                }
            }
        }

        List<object> originRecords = (List<object>)record["origins"];
        for (int originIndex = 0; originIndex < 2; originIndex++)
        {
            double volume = outwardSign * sums[originIndex] / 3.0;
            Require(IsFinite(sums[originIndex]) && IsFinite(volume), "Surface-divergence integral is non-finite.");
            result.Volumes[originIndex] = volume;
            originRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "origin_index", originIndex }, { "point_mm", (double[])origins[originIndex].Clone() },
                { "flux_integral_mm3", sums[originIndex] }, { "outward_sign_for_cross_du_dv", outwardSign },
                { "signed_volume_mm3", volume },
                { "formula", "outward_sign/3 * sum(dot(p-origin,cross(du,dv))*weightU*weightV)" }
            });
        }
        double mean = (result.Volumes[0] + result.Volumes[1]) * 0.5;
        double originDelta = Math.Abs(result.Volumes[0] - result.Volumes[1]);
        record["grid_mean_signed_volume_mm3"] = mean;
        record["two_origin_delta_mm3"] = originDelta;
        result.Summary = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "grid_index", index }, { "n", n }, { "offset_fraction", offset },
            { "sample_order", "i_u_outer_j_v_inner" }, { "sample_count", n * n },
            { "origin_signed_volumes_mm3", new double[] { result.Volumes[0], result.Volumes[1] } },
            { "grid_mean_signed_volume_mm3", mean }, { "two_origin_delta_mm3", originDelta }
        };
        return result;
    }

    private static bool EvaluateComparisons(GridResult[] grids, double existingToleranceMm3,
        Dictionary<string, object> summary, List<object> failures)
    {
        double limit = existingToleranceMm3 / 10.0;
        List<object> convergence = new List<object>();
        List<object> originChecks = new List<object>();
        bool pass = true;
        for (int offsetIndex = 0; offsetIndex < 2; offsetIndex++)
        {
            GridResult g32 = grids[offsetIndex];
            GridResult g64 = grids[2 + offsetIndex];
            GridResult g128 = grids[4 + offsetIndex];
            for (int originIndex = 0; originIndex < 2; originIndex++)
            {
                double delta3264 = Math.Abs(g64.Volumes[originIndex] - g32.Volumes[originIndex]);
                double delta64128 = Math.Abs(g128.Volumes[originIndex] - g64.Volumes[originIndex]);
                bool pass3264 = delta3264 < limit;
                bool pass64128 = delta64128 < limit;
                convergence.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "offset_fraction", g32.Offset }, { "origin_index", originIndex },
                    { "n32_to_n64_absolute_delta_mm3", delta3264 },
                    { "n32_to_n64_limit_mm3", limit }, { "n32_to_n64_passed", pass3264 },
                    { "n64_to_n128_absolute_delta_mm3", delta64128 },
                    { "n64_to_n128_limit_mm3", limit }, { "n64_to_n128_passed", pass64128 }
                });
                pass = pass && pass3264 && pass64128;
            }
        }
        Check(criteriaFor(summary), failures, "each_offset_32_to_64_and_64_to_128_refinement_delta",
            pass, convergence, "all origin-specific absolute deltas < original N04 tolerance / 10");

        bool allOriginDeltasPass = true;
        for (int gridIndex = 0; gridIndex < grids.Length; gridIndex++)
        {
            GridResult grid = grids[gridIndex];
            double delta = Math.Abs(grid.Volumes[0] - grid.Volumes[1]);
            bool thisPass = delta < limit;
            originChecks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "n", grid.N }, { "offset_fraction", grid.Offset },
                { "origin_0_volume_mm3", grid.Volumes[0] }, { "origin_1_volume_mm3", grid.Volumes[1] },
                { "absolute_delta_mm3", delta }, { "limit_mm3", limit }, { "passed", thisPass }
            });
            allOriginDeltasPass = allOriginDeltasPass && thisPass;
        }
        Check(criteriaFor(summary), failures, "every_mesh_two_origin_volume_delta",
            allOriginDeltasPass, originChecks,
            "every grid's absolute two-origin signed-volume delta < original N04 tolerance / 10");

        List<object> offsetChecks = new List<object>();
        bool offsetPass = true;
        for (int originIndex = 0; originIndex < 2; originIndex++)
        {
            double delta = Math.Abs(grids[4].Volumes[originIndex] - grids[5].Volumes[originIndex]);
            bool thisPass = delta < limit;
            offsetChecks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "origin_index", originIndex }, { "n", 128 },
                { "offset_0_25_volume_mm3", grids[4].Volumes[originIndex] },
                { "offset_0_75_volume_mm3", grids[5].Volumes[originIndex] },
                { "absolute_offset_delta_mm3", delta }, { "limit_mm3", limit }, { "passed", thisPass }
            });
            offsetPass = offsetPass && thisPass;
        }
        double meanOffsetDelta = Math.Abs(
            (grids[4].Volumes[0] + grids[4].Volumes[1]) * 0.5 -
            (grids[5].Volumes[0] + grids[5].Volumes[1]) * 0.5);
        bool meanOffsetPass = meanOffsetDelta < limit;
        offsetChecks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "comparison", "grid means" }, { "n", 128 }, { "offset_0_25_grid_mean_mm3", (grids[4].Volumes[0] + grids[4].Volumes[1]) * 0.5 },
            { "offset_0_75_grid_mean_mm3", (grids[5].Volumes[0] + grids[5].Volumes[1]) * 0.5 },
            { "absolute_offset_delta_mm3", meanOffsetDelta }, { "limit_mm3", limit }, { "passed", meanOffsetPass }
        });
        offsetPass = offsetPass && meanOffsetPass;
        Check(criteriaFor(summary), failures, "final_level_offset_delta",
            offsetPass, offsetChecks, "all n=128 origin-specific and mean offset deltas < original N04 tolerance / 10");

        summary["refinement_comparisons"] = convergence;
        summary["two_origin_comparisons"] = originChecks;
        summary["final_offset_comparisons"] = offsetChecks;
        summary["refinement_and_origin_limit_mm3"] = limit;
        return pass && allOriginDeltasPass && offsetPass;
    }

    private static List<object> criteriaFor(Dictionary<string, object> summary)
    {
        return (List<object>)summary["criteria"];
    }

    private sealed class FaceProps
    {
        public double[] Point;
        public double[] Du;
        public double[] Dv;
        public double[] Normal;
    }

    private static FaceProps ReadFaceProps(UFModl modl, Tag face, double[] uv)
    {
        FaceProps result = new FaceProps();
        result.Point = new double[3];
        result.Du = new double[3];
        result.Dv = new double[3];
        double[] duu = new double[3];
        double[] dvv = new double[3];
        result.Normal = new double[3];
        double[] radii = new double[2];
        modl.AskFaceProps(face, uv, result.Point, result.Du, result.Dv,
            duu, dvv, result.Normal, radii);
        EnsureFinite(uv, "Face parameter is non-finite.");
        EnsureFinite(result.Point, "AskFaceProps returned a non-finite position.");
        EnsureFinite(result.Du, "AskFaceProps returned a non-finite U derivative.");
        EnsureFinite(result.Dv, "AskFaceProps returned a non-finite V derivative.");
        EnsureFinite(result.Normal, "AskFaceProps returned a non-finite native normal.");
        return result;
    }

    private static void Check(List<object> criteria, List<object> failures,
        string name, bool passed, object actual, object expected)
    {
        criteria.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "passed", passed }, { "actual", actual }, { "expected", expected }
        });
        if (!passed) AddFailure(failures, "Criterion failed: " + name + ".");
    }

    private static void AddFailure(List<object> failures, string reason)
    {
        failures.Add(reason);
    }

    private static string[] TagStrings(Tag[] tags)
    {
        if (tags == null) return new string[0];
        string[] result = new string[tags.Length];
        for (int i = 0; i < tags.Length; i++) result[i] = tags[i].ToString();
        return result;
    }

    private static double[][] MatrixRows(double[,] matrix)
    {
        double[][] rows = new double[matrix.GetLength(0)][];
        for (int row = 0; row < matrix.GetLength(0); row++)
        {
            rows[row] = new double[matrix.GetLength(1)];
            for (int column = 0; column < matrix.GetLength(1); column++)
                rows[row][column] = matrix[row, column];
        }
        return rows;
    }

    private static void ComputeWorldBounds(double[] corner, double[,] axes,
        double[] lengths, out double[] minimum, out double[] maximum)
    {
        minimum = new double[] { Double.PositiveInfinity, Double.PositiveInfinity, Double.PositiveInfinity };
        maximum = new double[] { Double.NegativeInfinity, Double.NegativeInfinity, Double.NegativeInfinity };
        for (int mask = 0; mask < 8; mask++)
        {
            double[] point = (double[])corner.Clone();
            for (int axis = 0; axis < 3; axis++)
            {
                if ((mask & (1 << axis)) == 0) continue;
                for (int coordinate = 0; coordinate < 3; coordinate++)
                    point[coordinate] += axes[axis, coordinate] * lengths[axis];
            }
            for (int coordinate = 0; coordinate < 3; coordinate++)
            {
                minimum[coordinate] = Math.Min(minimum[coordinate], point[coordinate]);
                maximum[coordinate] = Math.Max(maximum[coordinate], point[coordinate]);
            }
        }
        EnsureFinite(minimum, "Exact box produced non-finite world minimum.");
        EnsureFinite(maximum, "Exact box produced non-finite world maximum.");
    }

    private static void AddCompensated(double value, ref double sum, ref double correction)
    {
        double adjusted = value - correction;
        double next = sum + adjusted;
        correction = (next - sum) - adjusted;
        sum = next;
    }

    private static double RelativeVectorDifference(double[] first, double[] second)
    {
        double denominator = Math.Max(Norm(first), Norm(second));
        if (denominator == 0.0) return Double.PositiveInfinity;
        return Distance(first, second) / denominator;
    }

    private static double[] Cross(double[] first, double[] second)
    {
        return new double[]
        {
            first[1] * second[2] - first[2] * second[1],
            first[2] * second[0] - first[0] * second[2],
            first[0] * second[1] - first[1] * second[0]
        };
    }

    private static double Dot(double[] first, double[] second)
    {
        return first[0] * second[0] + first[1] * second[1] + first[2] * second[2];
    }

    private static double Norm(double[] vector)
    {
        return Math.Sqrt(Dot(vector, vector));
    }

    private static double Distance(double[] first, double[] second)
    {
        return Norm(Subtract(first, second));
    }

    private static double[] Add(double[] first, double[] second)
    {
        return new double[] { first[0] + second[0], first[1] + second[1], first[2] + second[2] };
    }

    private static double[] Subtract(double[] first, double[] second)
    {
        return new double[] { first[0] - second[0], first[1] - second[1], first[2] - second[2] };
    }

    private static double[] Scale(double[] vector, double scale)
    {
        return new double[] { vector[0] * scale, vector[1] * scale, vector[2] * scale };
    }

    private static void Copy3(double[] source, double[] target, int start)
    {
        target[start] = source[0]; target[start + 1] = source[1]; target[start + 2] = source[2];
    }

    private static void EnsureFinite(double[] values, string message)
    {
        for (int i = 0; i < values.Length; i++)
            if (!IsFinite(values[i])) throw new InvalidOperationException(message);
    }

    private static bool IsFinite(double value)
    {
        return !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Dictionary<string, object> Map()
    {
        return new Dictionary<string, object>(StringComparer.Ordinal);
    }

    private static void WriteEvidence(string path, object value)
    {
        if (String.IsNullOrEmpty(path)) throw new InvalidOperationException("Evidence path is null or empty.");
        string directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        StringBuilder json = new StringBuilder(65536);
        AppendJson(json, value);
        File.WriteAllText(path, json.ToString() + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void AppendJson(StringBuilder output, object value)
    {
        if (value == null) { output.Append("null"); return; }
        string text = value as string;
        if (text != null) { AppendQuoted(output, text); return; }
        if (value is bool) { output.Append((bool)value ? "true" : "false"); return; }
        if (value is double)
        {
            double number = (double)value;
            if (IsFinite(number)) output.Append(number.ToString("R", CultureInfo.InvariantCulture));
            else AppendQuoted(output, "NONFINITE:" + number.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        if (value is float)
        {
            float number = (float)value;
            if (!Single.IsNaN(number) && !Single.IsInfinity(number)) output.Append(number.ToString("R", CultureInfo.InvariantCulture));
            else AppendQuoted(output, "NONFINITE:" + number.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        if (value is byte || value is sbyte || value is short || value is ushort || value is int ||
            value is uint || value is long || value is ulong || value is decimal)
        {
            output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }
        IDictionary<string, object> map = value as IDictionary<string, object>;
        if (map != null)
        {
            output.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> item in map)
            {
                if (!first) output.Append(',');
                first = false;
                AppendQuoted(output, item.Key);
                output.Append(':');
                AppendJson(output, item.Value);
            }
            output.Append('}');
            return;
        }
        IEnumerable sequence = value as IEnumerable;
        if (sequence != null)
        {
            output.Append('[');
            bool first = true;
            foreach (object item in sequence)
            {
                if (!first) output.Append(',');
                first = false;
                AppendJson(output, item);
            }
            output.Append(']');
            return;
        }
        AppendQuoted(output, Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static void AppendQuoted(StringBuilder output, string value)
    {
        output.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') output.Append("\\\"");
            else if (c == '\\') output.Append("\\\\");
            else if (c == '\b') output.Append("\\b");
            else if (c == '\f') output.Append("\\f");
            else if (c == '\n') output.Append("\\n");
            else if (c == '\r') output.Append("\\r");
            else if (c == '\t') output.Append("\\t");
            else if (c < 0x20) output.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else output.Append(c);
        }
        output.Append('"');
    }
}
