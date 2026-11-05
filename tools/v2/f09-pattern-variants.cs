// F09 NXOpen SketchPatternBuilder variants; entry point is F09PatternVariants.Run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using NXOpen;
using NXOpen.GeometricUtilities;

public static class F09PatternVariants
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";

    private sealed class Variant
    {
        internal string Name;
        internal bool SetXSelection;
        internal bool SelectionBeforeDirection;
        internal string UpdateSequence;
        internal bool CreateConstraintDuringEvaluation;
        internal bool CreateConstraintAtCommit;
        internal bool CreateSpacingExpression;
        internal bool LockOrientation;
    }

    public static void Run()
    {
        string runId = Guid.NewGuid().ToString("N");
        string modelsDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "models");
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        Directory.CreateDirectory(modelsDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        string partPath = Path.Combine(modelsDirectory, "F09-pattern-variants-" + runId + ".prt");
        string reportPath = Path.Combine(evidenceDirectory, "F09-pattern-variants-" + runId + ".json");
        Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> cases = new List<object>();
        report.Add("test", "F09 NXOpen SketchPatternBuilder parameter and update-order variants");
        report.Add("run_id", runId);
        report.Add("started_utc", DateTime.UtcNow.ToString("o"));
        report.Add("part_path", partPath);
        report.Add("report_path", reportPath);
        report.Add("fresh_batch_part_only", true);
        report.Add("mutates_existing_model", false);
            report.Add("production_builder", "NXOpen SketchPatternBuilder, with CreateSketchPatternBuilder(null)");
            report.Add("artifact_scope", "unique saved scratch part; never opens or mutates a preexisting part");
        report.Add("source_curve", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", "Line" }, { "start_mm", new double[] { 10.0, 2.0, 0.0 } }, { "end_mm", new double[] { 14.0, 2.0, 0.0 } }
        });
        report.Add("scratch_setup", "XY geometry-only sketch via the same existing-plane, specified-origin SketchInPlaceBuilder settings and InferNoConstraints line insertion as M3NxFeatureFactory; unique part is saved before variants.");
        report.Add("pattern_request", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "pattern_type", "Linear" }, { "direction_xy", new double[] { 1.0, 0.0 } },
            { "spacing_mm", 18.0 }, { "positions_including_source", 3 }
        });
        report.Add("cases", cases);
        report.Add("scope", new string[] { "native NX builder API variants", "not release acceptance", "every variant is undone to its mark" });
        WriteReport(reportPath, report);

        Session session = Session.GetSession();
        Part part = null;
        Sketch sketch = null;
        try
        {
            BasePart oldWork = session.Parts.Work;
            report["preexisting_work_part_path"] = oldWork == null ? null : oldWork.FullPath;
            report["preexisting_work_part_modified"] = oldWork == null ? (object)null : oldWork.IsModified;
            part = session.Parts.NewDisplay(partPath, Part.Units.Millimeters);
            sketch = CreateSketch(part);
            sketch.Activate(Sketch.ViewReorient.False);
            Line source = part.Curves.CreateLine(new Point3d(10.0, 2.0, 0.0), new Point3d(14.0, 2.0, 0.0));
            sketch.AddGeometry(source, Sketch.InferConstraintsOption.InferNoConstraints);
            sketch.Update();
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            report["target_sketch_tag"] = sketch.Tag.ToString();
            report["target_sketch_id"] = sketch.JournalIdentifier;
            report["source_curve_tag"] = source.Tag.ToString();
            report["source_curve_id"] = source.JournalIdentifier;
            report["initial_part_state"] = CapturePartState(part, sketch);
            WriteReport(reportPath, report);

            PartSaveStatus initialSave = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
            report["initial_save"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "unsaved_parts", initialSave.NumberUnsavedParts }, { "unsaved_objects", initialSave.NumberUnsavedObjects }
            };
            initialSave.Dispose();
            report["initial_part_state_after_save"] = CapturePartState(part, sketch);
            WriteReport(reportPath, report);

            Variant[] variants = new Variant[]
            {
                new Variant { Name = "minimal_evaluate_only_no_XSelection_no_updates", SetXSelection = false,
                    UpdateSequence = "evaluate_only", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = false },
                new Variant { Name = "direction_only_without_XSelection", SetXSelection = false,
                    UpdateSequence = "linear_update_input_copies_evaluate", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = true },
                new Variant { Name = "selection_then_direction", SetXSelection = true, SelectionBeforeDirection = true,
                    UpdateSequence = "selection_then_direction_linear_update_input_copies_evaluate", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = true },
                new Variant { Name = "input_first_then_linear_update", SetXSelection = true,
                    UpdateSequence = "input_linear_update_copies_evaluate", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = true },
                new Variant { Name = "update_data_after_direction", SetXSelection = true,
                    UpdateSequence = "linear_update_input_data_copies_evaluate", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = true },
                new Variant { Name = "update_data_before_input", SetXSelection = true,
                    UpdateSequence = "linear_update_data_input_copies_evaluate", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = true },
                new Variant { Name = "evaluate_commit_without_update_calls", SetXSelection = true,
                    UpdateSequence = "evaluate_only", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = true },
                new Variant { Name = "constraint_true_for_evaluation_false_at_commit", SetXSelection = true,
                    UpdateSequence = "linear_update_input_copies_evaluate", CreateConstraintDuringEvaluation = true,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = true },
                new Variant { Name = "constraint_true_through_commit_diagnostic", SetXSelection = true,
                    UpdateSequence = "linear_update_input_copies_evaluate", CreateConstraintDuringEvaluation = true,
                    CreateConstraintAtCommit = true, CreateSpacingExpression = false, LockOrientation = true },
                new Variant { Name = "spacing_expression_true_diagnostic", SetXSelection = true,
                    UpdateSequence = "linear_update_input_copies_evaluate", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = true, LockOrientation = true },
                new Variant { Name = "lock_orientation_false", SetXSelection = true,
                    UpdateSequence = "linear_update_input_copies_evaluate", CreateConstraintDuringEvaluation = false,
                    CreateConstraintAtCommit = false, CreateSpacingExpression = false, LockOrientation = false },
            };

            foreach (Variant variant in variants)
            {
                Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal);
                cases.Add(row);
                RunVariant(session, part, sketch, source, variant, row, reportPath, report);
                report["completed_case_count"] = cases.Count;
                WriteReport(reportPath, report);
            }

            PartSaveStatus finalSave = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
            report["final_save"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "unsaved_parts", finalSave.NumberUnsavedParts }, { "unsaved_objects", finalSave.NumberUnsavedObjects }
            };
            finalSave.Dispose();
            report["final_part_state"] = CapturePartState(part, sketch);
            report["completed_utc"] = DateTime.UtcNow.ToString("o");
            report["passed"] = cases.Count == variants.Length &&
                cases.Cast<Dictionary<string, object>>().All(delegate(Dictionary<string, object> item)
                {
                    return Boolean.Equals(item.ContainsKey("undo_to_mark_succeeded") ? item["undo_to_mark_succeeded"] : null, true) &&
                        Boolean.Equals(item.ContainsKey("undo_geometry_restored") ? item["undo_geometry_restored"] : null, true);
                });
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            report["completed_utc"] = DateTime.UtcNow.ToString("o");
            report["passed"] = false;
        }
        finally
        {
            WriteReport(reportPath, report);
        }
    }

    private static void RunVariant(Session session, Part part, Sketch sketch, Line source,
        Variant variant, Dictionary<string, object> row, string reportPath,
        Dictionary<string, object> report)
    {
        row["name"] = variant.Name;
        row["success"] = false;
        row["update_sequence"] = variant.UpdateSequence;
        row["x_selection_enabled"] = variant.SetXSelection;
        Stage(reportPath, report, row, "variant_entry");
        Stage(reportPath, report, row, "before_SetUndoMark");
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
            "F09 builder variant " + variant.Name);
        Stage(reportPath, report, row, "after_SetUndoMark");
        SketchPatternBuilder builder = null;
        Exception operationException = null;
        try
        {
            Stage(reportPath, report, row, "before_CapturePartState_before");
            row["part_state_before"] = CapturePartState(part, sketch);
            Stage(reportPath, report, row, "after_CapturePartState_before");
            Stage(reportPath, report, row, "before_CurveRecord_source");
            row["source_line_before"] = CurveRecord(source);
            Stage(reportPath, report, row, "after_CurveRecord_source");

            Stage(reportPath, report, row, "before_Sketch_Activate");
            sketch.Activate(Sketch.ViewReorient.False);
            Stage(reportPath, report, row, "after_Sketch_Activate");
            Stage(reportPath, report, row, "before_CreateSketchPatternBuilder");
            builder = part.Sketches.CreateSketchPatternBuilder(null);
            Stage(reportPath, report, row, "after_CreateSketchPatternBuilder");
            row["builder_type"] = builder.GetType().FullName;

            Stage(reportPath, report, row, "before_AddSourceSection");
            AddSourceSection(part, builder.Section, source, reportPath, report, row);
            Stage(reportPath, report, row, "after_AddSourceSection");
            Stage(reportPath, report, row, "before_get_PatternService");
            PatternDefinition service = builder.PatternService;
            Stage(reportPath, report, row, "after_get_PatternService");
            Stage(reportPath, report, row, "before_set_PatternType");
            service.PatternType = PatternDefinition.PatternEnum.Linear;
            Stage(reportPath, report, row, "after_set_PatternType");
            Stage(reportPath, report, row, "before_get_PatternOrientation");
            PatternOrientation orientation = service.PatternOrientation;
            Stage(reportPath, report, row, "after_get_PatternOrientation");
            Stage(reportPath, report, row, "before_set_LinearOrientationOption");
            orientation.LinearOrientationOption = PatternOrientation.Enum.Fixed;
            Stage(reportPath, report, row, "after_set_LinearOrientationOption");
            Stage(reportPath, report, row, "before_get_RectangularDefinition");
            RectangularPattern rectangular = service.RectangularDefinition;
            Stage(reportPath, report, row, "after_get_RectangularDefinition");
            Stage(reportPath, report, row, "before_set_UseYDirectionToggle");
            rectangular.UseYDirectionToggle = false;
            Stage(reportPath, report, row, "after_set_UseYDirectionToggle");
            Stage(reportPath, report, row, "before_set_CreateConstraint");
            builder.CreateConstraint = variant.CreateConstraintDuringEvaluation;
            Stage(reportPath, report, row, "after_set_CreateConstraint");
            Stage(reportPath, report, row, "before_set_CreateSpacingExp");
            builder.CreateSpacingExp = variant.CreateSpacingExpression;
            Stage(reportPath, report, row, "after_set_CreateSpacingExp");
            Stage(reportPath, report, row, "before_set_LockOrientation");
            builder.LockOrientation = variant.LockOrientation;
            Stage(reportPath, report, row, "after_set_LockOrientation");
            Stage(reportPath, report, row, "before_CreateDirection");
            Direction direction = part.Directions.CreateDirection(sketch.Origin,
                new Vector3d(1.0, 0.0, 0.0), SmartObject.UpdateOption.WithinModeling);
            Stage(reportPath, report, row, "after_CreateDirection");
            if (variant.SelectionBeforeDirection)
            {
                Stage(reportPath, report, row, "before_set_XSelection_Value_before_XDirection");
                rectangular.XSelection.Value = direction;
                Stage(reportPath, report, row, "after_set_XSelection_Value_before_XDirection");
                Stage(reportPath, report, row, "before_set_XDirection_after_XSelection");
                rectangular.XDirection = direction;
                Stage(reportPath, report, row, "after_set_XDirection_after_XSelection");
            }
            else
            {
                Stage(reportPath, report, row, "before_set_XDirection");
                rectangular.XDirection = direction;
                Stage(reportPath, report, row, "after_set_XDirection");
                if (variant.SetXSelection)
                {
                    Stage(reportPath, report, row, "before_set_XSelection_Value");
                    rectangular.XSelection.Value = direction;
                    Stage(reportPath, report, row, "after_set_XSelection_Value");
                }
            }
            Stage(reportPath, report, row, "before_set_NCopies_RightHandSide");
            rectangular.XSpacing.NCopies.RightHandSide = "3";
            Stage(reportPath, report, row, "after_set_NCopies_RightHandSide");
            Stage(reportPath, report, row, "before_set_PitchDistance_RightHandSide");
            rectangular.XSpacing.PitchDistance.RightHandSide = "18";
            Stage(reportPath, report, row, "after_set_PitchDistance_RightHandSide");

            Stage(reportPath, report, row, "before_ReadSettings_before_update");
            row["settings_before_update"] = ReadSettings(builder, service, rectangular, direction,
                reportPath, report, row);
            Stage(reportPath, report, row, "after_ReadSettings_before_update");
            ApplyUpdates(builder, variant.UpdateSequence, row, reportPath, report);
            Stage(reportPath, report, row, "before_ReadSettings_after_evaluate");
            row["settings_after_evaluate"] = ReadSettings(builder, service, rectangular, direction,
                reportPath, report, row);
            Stage(reportPath, report, row, "after_ReadSettings_after_evaluate");
            if (!variant.CreateConstraintAtCommit)
            {
                Stage(reportPath, report, row, "before_set_CreateConstraint_false_at_commit");
                builder.CreateConstraint = false;
                Stage(reportPath, report, row, "after_set_CreateConstraint_false_at_commit");
            }
            if (!variant.CreateSpacingExpression)
            {
                Stage(reportPath, report, row, "before_set_CreateSpacingExp_false_at_commit");
                builder.CreateSpacingExp = false;
                Stage(reportPath, report, row, "after_set_CreateSpacingExp_false_at_commit");
            }
            Stage(reportPath, report, row, "before_ReadSettings_before_commit");
            row["settings_before_commit"] = ReadSettings(builder, service, rectangular, direction,
                reportPath, report, row);
            Stage(reportPath, report, row, "after_ReadSettings_before_commit");
            Stage(reportPath, report, row, "before_Commit");
            NXObject committed = builder.Commit();
            Stage(reportPath, report, row, "after_Commit");
            row["commit_returned_null"] = committed == null;
            Stage(reportPath, report, row, "before_ObjectRecord_commit");
            row["commit_object"] = ObjectRecord(committed);
            Stage(reportPath, report, row, "after_ObjectRecord_commit");
            Stage(reportPath, report, row, "before_GetCommittedObjects");
            NXObject[] committedObjects = builder.GetCommittedObjects();
            Stage(reportPath, report, row, "after_GetCommittedObjects");
            row["committed_objects"] = ObjectRecords(committedObjects);
            Stage(reportPath, report, row, "after_ObjectRecords_committed_objects");
            Stage(reportPath, report, row, "before_ReadSettings_after_commit");
            row["settings_after_commit"] = ReadSettings(builder, service, rectangular, direction,
                reportPath, report, row);
            Stage(reportPath, report, row, "after_ReadSettings_after_commit");
            Stage(reportPath, report, row, "before_Sketch_Update");
            sketch.Update();
            Stage(reportPath, report, row, "after_Sketch_Update");
            Stage(reportPath, report, row, "before_Sketch_Deactivate");
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            Stage(reportPath, report, row, "after_Sketch_Deactivate");
            Stage(reportPath, report, row, "before_DoUpdate");
            int updateErrors = session.UpdateManager.DoUpdate(mark);
            Stage(reportPath, report, row, "after_DoUpdate");
            row["update_error_count"] = updateErrors;
            Stage(reportPath, report, row, "before_CapturePartState_after_update");
            row["part_state_after_update"] = CapturePartState(part, sketch);
            Stage(reportPath, report, row, "after_CapturePartState_after_update");
            row["target_sketch_geometry_tag_delta"] = NewSketchGeometryTags(row["part_state_before"], row["part_state_after_update"]);
            row["global_curve_tag_delta"] = NewCurveTags(row["part_state_before"], row["part_state_after_update"]);
            row["success"] = updateErrors == 0;
        }
        catch (Exception ex)
        {
            operationException = ex;
            row["error"] = ex.ToString();
            Stage(reportPath, report, row, "operation_exception_caught");
            try
            {
                Stage(reportPath, report, row, "before_get_ActiveSketch_in_exception_recovery");
                Sketch active = session.ActiveSketch;
                Stage(reportPath, report, row, "after_get_ActiveSketch_in_exception_recovery");
                if (active != null && sketch != null && active.Tag.Equals(sketch.Tag))
                {
                    Stage(reportPath, report, row, "before_exception_recovery_Deactivate");
                    active.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                    Stage(reportPath, report, row, "after_exception_recovery_Deactivate");
                }
            }
            catch (Exception recoveryException)
            {
                row["exception_recovery_error"] = recoveryException.ToString();
                Stage(reportPath, report, row, "exception_recovery_exception_caught");
            }
        }

        if (builder != null)
        {
            try
            {
                Stage(reportPath, report, row, "before_Builder_Destroy");
                builder.Destroy();
                Stage(reportPath, report, row, "after_Builder_Destroy");
            }
            catch (Exception ex)
            {
                row["builder_destroy_error"] = ex.ToString();
                if (operationException == null) row["success"] = false;
                Stage(reportPath, report, row, "Builder_Destroy_exception_caught");
            }
        }
        try
        {
            Stage(reportPath, report, row, "before_UndoToMark");
            session.UndoToMark(mark, null);
            row["undo_to_mark_succeeded"] = true;
            Stage(reportPath, report, row, "after_UndoToMark");
        }
        catch (Exception ex)
        {
            row["undo_to_mark_succeeded"] = false;
            row["undo_error"] = ex.ToString();
            Stage(reportPath, report, row, "UndoToMark_exception_caught");
        }
        try
        {
            Stage(reportPath, report, row, "before_DeleteUndoMark");
            session.DeleteUndoMark(mark, null);
            row["undo_mark_deleted"] = true;
            Stage(reportPath, report, row, "after_DeleteUndoMark");
        }
        catch (Exception ex)
        {
            row["undo_mark_deleted"] = false;
            row["delete_undo_mark_error"] = ex.ToString();
            Stage(reportPath, report, row, "DeleteUndoMark_exception_caught");
        }
        try
        {
            Stage(reportPath, report, row, "before_CapturePartState_after_undo");
            row["part_state_after_undo"] = CapturePartState(part, sketch);
            Stage(reportPath, report, row, "after_CapturePartState_after_undo");
            row["undo_geometry_restored"] = SameTags(row["part_state_before"], row["part_state_after_undo"]);
        }
        catch (Exception ex)
        {
            row["undo_readback_error"] = ex.ToString();
            row["undo_geometry_restored"] = false;
            Stage(reportPath, report, row, "post_undo_readback_exception_caught");
        }
        Stage(reportPath, report, row, "variant_complete");
    }

    private static void AddSourceSection(Part part, Section section, Line source,
        string reportPath, Dictionary<string, object> report, Dictionary<string, object> row)
    {
        Stage(reportPath, report, row, "before_Section_SetAllowedEntityTypes");
        section.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        Stage(reportPath, report, row, "after_Section_SetAllowedEntityTypes");
        Stage(reportPath, report, row, "before_Section_AllowSelfIntersection");
        section.AllowSelfIntersection(false);
        Stage(reportPath, report, row, "after_Section_AllowSelfIntersection");
        Stage(reportPath, report, row, "before_CreateRuleCurveDumb");
        SelectionIntentRule rule = part.ScRuleFactory.CreateRuleCurveDumb(new Curve[] { source });
        Stage(reportPath, report, row, "after_CreateRuleCurveDumb");
        Stage(reportPath, report, row, "before_Section_AddToSection");
        section.AddToSection(new SelectionIntentRule[] { rule }, source, null, null,
            source.StartPoint, Section.Mode.Create, false);
        Stage(reportPath, report, row, "after_Section_AddToSection");
    }

    private static void ApplyUpdates(SketchPatternBuilder builder, string sequence, Dictionary<string, object> row,
        string reportPath, Dictionary<string, object> report)
    {
        List<object> called = new List<object>();
        try
        {
            if (sequence == "evaluate_only")
            {
                Stage(reportPath, report, row, "before_EvaluatePattern");
                builder.EvaluatePattern();
                called.Add("EvaluatePattern");
                row["update_calls"] = called;
                Stage(reportPath, report, row, "after_EvaluatePattern");
            }
            else if (sequence == "input_linear_update_copies_evaluate")
            {
                Stage(reportPath, report, row, "before_UpdateInputSection");
                builder.UpdateInputSection(); called.Add("UpdateInputSection"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateInputSection");
                Stage(reportPath, report, row, "before_UpdateLinearDirectionObject");
                builder.UpdateLinearDirectionObject(); called.Add("UpdateLinearDirectionObject"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateLinearDirectionObject");
                Stage(reportPath, report, row, "before_UpdateCopies");
                builder.UpdateCopies(); called.Add("UpdateCopies"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateCopies");
                Stage(reportPath, report, row, "before_EvaluatePattern");
                builder.EvaluatePattern(); called.Add("EvaluatePattern"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_EvaluatePattern");
            }
            else if (sequence == "linear_update_input_copies_evaluate")
            {
                Stage(reportPath, report, row, "before_UpdateLinearDirectionObject");
                builder.UpdateLinearDirectionObject(); called.Add("UpdateLinearDirectionObject"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateLinearDirectionObject");
                Stage(reportPath, report, row, "before_UpdateInputSection");
                builder.UpdateInputSection(); called.Add("UpdateInputSection"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateInputSection");
                Stage(reportPath, report, row, "before_UpdateCopies");
                builder.UpdateCopies(); called.Add("UpdateCopies"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateCopies");
                Stage(reportPath, report, row, "before_EvaluatePattern");
                builder.EvaluatePattern(); called.Add("EvaluatePattern"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_EvaluatePattern");
            }
            else if (sequence == "linear_update_input_data_copies_evaluate")
            {
                Stage(reportPath, report, row, "before_UpdateLinearDirectionObject");
                builder.UpdateLinearDirectionObject(); called.Add("UpdateLinearDirectionObject"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateLinearDirectionObject");
                Stage(reportPath, report, row, "before_UpdateInputSection");
                builder.UpdateInputSection(); called.Add("UpdateInputSection"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateInputSection");
                Stage(reportPath, report, row, "before_UpdateData");
                builder.UpdateData(); called.Add("UpdateData"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateData");
                Stage(reportPath, report, row, "before_UpdateCopies");
                builder.UpdateCopies(); called.Add("UpdateCopies"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateCopies");
                Stage(reportPath, report, row, "before_EvaluatePattern");
                builder.EvaluatePattern(); called.Add("EvaluatePattern"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_EvaluatePattern");
            }
            else if (sequence == "linear_update_data_input_copies_evaluate")
            {
                Stage(reportPath, report, row, "before_UpdateLinearDirectionObject");
                builder.UpdateLinearDirectionObject(); called.Add("UpdateLinearDirectionObject"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateLinearDirectionObject");
                Stage(reportPath, report, row, "before_UpdateData");
                builder.UpdateData(); called.Add("UpdateData"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateData");
                Stage(reportPath, report, row, "before_UpdateInputSection");
                builder.UpdateInputSection(); called.Add("UpdateInputSection"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateInputSection");
                Stage(reportPath, report, row, "before_UpdateCopies");
                builder.UpdateCopies(); called.Add("UpdateCopies"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateCopies");
                Stage(reportPath, report, row, "before_EvaluatePattern");
                builder.EvaluatePattern(); called.Add("EvaluatePattern"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_EvaluatePattern");
            }
            else if (sequence == "selection_then_direction_linear_update_input_copies_evaluate")
            {
                Stage(reportPath, report, row, "before_UpdateLinearDirectionObject");
                builder.UpdateLinearDirectionObject(); called.Add("UpdateLinearDirectionObject"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateLinearDirectionObject");
                Stage(reportPath, report, row, "before_UpdateInputSection");
                builder.UpdateInputSection(); called.Add("UpdateInputSection"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateInputSection");
                Stage(reportPath, report, row, "before_UpdateCopies");
                builder.UpdateCopies(); called.Add("UpdateCopies"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_UpdateCopies");
                Stage(reportPath, report, row, "before_EvaluatePattern");
                builder.EvaluatePattern(); called.Add("EvaluatePattern"); row["update_calls"] = called;
                Stage(reportPath, report, row, "after_EvaluatePattern");
            }
            else
            {
                throw new ArgumentException("unknown update sequence " + sequence);
            }
            row["update_calls"] = called;
        }
        catch (Exception ex)
        {
            row["update_calls"] = called;
            row["update_exception"] = ex.ToString();
            Stage(reportPath, report, row, "update_exception_caught");
            throw;
        }
    }

    private static Dictionary<string, object> ReadSettings(SketchPatternBuilder builder,
        PatternDefinition service, RectangularPattern rectangular, Direction requestedDirection,
        string reportPath, Dictionary<string, object> report, Dictionary<string, object> row)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        Stage(reportPath, report, row, "before_ReadSettings_pattern_flags");
        result["pattern_type"] = service.PatternType.ToString();
        result["create_constraint"] = builder.CreateConstraint;
        result["create_spacing_expression"] = builder.CreateSpacingExp;
        result["lock_orientation"] = builder.LockOrientation;
        result["use_y_direction_toggle"] = rectangular.UseYDirectionToggle;
        Stage(reportPath, report, row, "after_ReadSettings_pattern_flags");
        Stage(reportPath, report, row, "before_ReadSettings_spacing_rhs_values");
        result["ncopies_rhs"] = rectangular.XSpacing.NCopies.RightHandSide;
        result["ncopies_value"] = rectangular.XSpacing.NCopies.Value;
        result["pitch_rhs"] = rectangular.XSpacing.PitchDistance.RightHandSide;
        result["pitch_value"] = rectangular.XSpacing.PitchDistance.Value;
        Stage(reportPath, report, row, "after_ReadSettings_spacing_rhs_values");
        Stage(reportPath, report, row, "before_ReadSettings_direction_tags");
        result["requested_direction_tag"] = requestedDirection.Tag.ToString();
        Direction actualDirection = rectangular.XDirection;
        result["x_direction_tag"] = actualDirection == null ? null : actualDirection.Tag.ToString();
        Stage(reportPath, report, row, "after_ReadSettings_direction_tags");
        return result;
    }

    private static object ObjectRecord(NXObject item)
    {
        if (item == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", item.GetType().FullName }, { "tag", item.Tag.ToString() }, { "journal_id", item.JournalIdentifier }
        };
    }

    private static List<object> ObjectRecords(NXObject[] objects)
    {
        List<object> records = new List<object>();
        if (objects == null) return records;
        foreach (NXObject item in objects) records.Add(ObjectRecord(item));
        return records;
    }

    private static Sketch CreateSketch(Part part)
    {
        Point3d origin = new Point3d(0.0, 0.0, 0.0);
        Plane plane = part.Planes.CreatePlane(origin, new Vector3d(0.0, 0.0, 1.0), SmartObject.UpdateOption.WithinModeling);
        SketchInPlaceBuilder builder = part.Sketches.CreateSketchInPlaceBuilder2(null);
        Sketch sketch = null;
        try
        {
            builder.PlaneReference = plane;
            builder.AxisReference = part.Directions.CreateDirection(origin, new Vector3d(1.0, 0.0, 0.0), SmartObject.UpdateOption.WithinModeling);
            builder.SketchOrigin = part.Points.CreatePoint(origin);
            builder.PlaneOption = Sketch.PlaneOption.ExistingPlane;
            builder.OriginOption = OriginMethod.SpecifyPoint;
            sketch = (Sketch)builder.Commit();
        }
        finally
        {
            builder.Destroy();
        }
        if (sketch == null) throw new InvalidOperationException("NX did not create the F09 probe Sketch");
        return sketch;
    }

    private static Dictionary<string, object> CapturePartState(Part part, Sketch sketch)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "modified", part.IsModified },
            { "sketch_geometry", GeometryRecords(sketch.GetAllGeometry()) },
            { "part_curves", CurveRecords(part.Curves.ToArray()) },
            { "features", ObjectRecords(part.Features.GetFeatures()) },
            { "expressions", ObjectRecords(part.Expressions.ToArray()) },
            { "points", ObjectRecords(part.Points.ToArray()) },
            { "axes", ObjectRecords(part.Axes.ToArray()) },
            { "directions", ObjectRecords(part.Directions.ToArray()) },
            { "sketch_constraint_count", ConstraintCount(sketch) }
        };
    }

    private static int ConstraintCount(Sketch sketch)
    {
        int count = 0;
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (constraints == null) throw new InvalidOperationException("Sketch.GetAllConstraintsOfType returned null");
            count += constraints.Length;
        }
        return count;
    }

    private static List<object> GeometryRecords(NXObject[] objects)
    {
        List<object> records = new List<object>();
        if (objects == null) return records;
        foreach (NXObject item in objects) records.Add(CurveRecord(item as Curve));
        return records;
    }

    private static List<object> CurveRecords(Curve[] curves)
    {
        List<object> records = new List<object>();
        if (curves == null) return records;
        foreach (Curve curve in curves) records.Add(CurveRecord(curve));
        return records;
    }

    private static object CurveRecord(Curve curve)
    {
        if (curve == null) return null;
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", curve.GetType().Name }, { "tag", curve.Tag.ToString() }, { "journal_id", curve.JournalIdentifier }
        };
        Line line = curve as Line;
        if (line != null)
        {
            record["start"] = PointRecord(line.StartPoint);
            record["end"] = PointRecord(line.EndPoint);
            record["length"] = line.GetLength();
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            record["center"] = PointRecord(arc.CenterPoint);
            record["radius"] = arc.Radius;
            record["start_angle"] = arc.StartAngle;
            record["end_angle"] = arc.EndAngle;
            record["length"] = arc.GetLength();
        }
        return record;
    }

    private static List<object> PointRecord(Point3d point)
    {
        return new List<object> { point.X, point.Y, point.Z };
    }

    private static List<string> NewSketchGeometryTags(object beforeValue, object afterValue)
    {
        Dictionary<string, object> before = beforeValue as Dictionary<string, object>;
        Dictionary<string, object> after = afterValue as Dictionary<string, object>;
        if (before == null || after == null) return new List<string>();
        return NewTags(before["sketch_geometry"] as List<object>, after["sketch_geometry"] as List<object>);
    }

    private static List<string> NewCurveTags(object beforeValue, object afterValue)
    {
        Dictionary<string, object> before = beforeValue as Dictionary<string, object>;
        Dictionary<string, object> after = afterValue as Dictionary<string, object>;
        if (before == null || after == null) return new List<string>();
        return NewTags(before["part_curves"] as List<object>, after["part_curves"] as List<object>);
    }

    private static List<string> NewTags(List<object> before, List<object> after)
    {
        HashSet<string> tagsBefore = ReadTagSet(before);
        HashSet<string> tagsAfter = ReadTagSet(after);
        List<string> result = new List<string>();
        foreach (string tag in tagsAfter) if (!tagsBefore.Contains(tag)) result.Add(tag);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static HashSet<string> ReadTagSet(List<object> records)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        if (records == null) return result;
        foreach (object value in records)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record == null) continue;
            object tag;
            if (record.TryGetValue("tag", out tag) && tag != null) result.Add(Convert.ToString(tag, System.Globalization.CultureInfo.InvariantCulture));
        }
        return result;
    }

    private static bool SameTags(object beforeValue, object afterValue)
    {
        Dictionary<string, object> before = beforeValue as Dictionary<string, object>;
        Dictionary<string, object> after = afterValue as Dictionary<string, object>;
        if (before == null || after == null) return false;
        foreach (string key in new string[] { "sketch_geometry", "part_curves", "features", "expressions", "points", "axes", "directions" })
        {
            HashSet<string> left = ReadTagSet(before[key] as List<object>);
            HashSet<string> right = ReadTagSet(after[key] as List<object>);
            if (!left.SetEquals(right)) return false;
        }
        object leftCount = before["sketch_constraint_count"];
        object rightCount = after["sketch_constraint_count"];
        return Convert.ToInt32(leftCount) == Convert.ToInt32(rightCount);
    }

    private static void WriteReport(string path, Dictionary<string, object> report)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        report["updated_utc"] = DateTime.UtcNow.ToString("o");
        File.WriteAllText(path, serializer.Serialize(report), new System.Text.UTF8Encoding(false));
    }

    private static void Stage(string reportPath, Dictionary<string, object> report,
        Dictionary<string, object> row, string stage)
    {
        object historyObject;
        List<object> history;
        if (!row.TryGetValue("stage_history", out historyObject) || (history = historyObject as List<object>) == null)
        {
            history = new List<object>();
            row["stage_history"] = history;
        }
        history.Add(stage);
        string now = DateTime.UtcNow.ToString("o");
        row["last_stage"] = stage;
        row["last_stage_utc"] = now;
        report["active_case"] = row.ContainsKey("name") ? row["name"] : null;
        report["last_native_stage"] = stage;
        report["last_native_stage_utc"] = now;
        WriteReport(reportPath, report);
    }
}
