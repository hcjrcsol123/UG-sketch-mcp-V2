"""GUI-only F11 solver discovery in the disposable model, not acceptance."""
import json
import os
from pathlib import Path
import traceback
import uuid
import NXOpen
import NXOpen.UF

root = Path(r'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp')
output = root / 'workspace/v2/evidence' / ('F11-gui-python-solver-' + uuid.uuid4().hex + '.json')
report = {'scope': 'GUI API discovery only', 'pid': os.getpid(), 'sketches': []}
def read(sketch):
    status, dof = sketch.GetStatus()
    names = ['Unknown','NotEvaluated','UnderConstrained','WellConstrained','OverConstrained','InconsistentlyConstrained']
    return {'status': next((n for n in names if status == getattr(NXOpen.Sketch.Status,n)), 'UNMAPPED'),
            'dof': dof, 'uses_legacy_solver': sketch.UsesLegacySolver,
            'upgrade_upon_activation': sketch.UpgradeUponActivation}
try:
    session = NXOpen.Session.GetSession()
    part = session.Parts.Work
    report.update(application=session.ApplicationName, task_environment=session.IsInTaskEnvironment)
    if part is None or not part.FullPath.startswith(str(root / 'workspace/v2/models/F11-')) or session.ActiveSketch is not None:
        raise RuntimeError('Requires idle disposable F11 model')
    report['part_path'] = part.FullPath
    for sketch, variant in [(sk, v) for sk in part.Sketches for v in ['show_movable_region']]:
        row = {'id':sketch.JournalIdentifier, 'variant':variant, 'before':read(sketch)}
        report['sketches'].append(row)
        mark = session.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible, 'F11 GUI discovery')
        began_task = False
        preference_before = session.Preferences.Sketch.FindMovableObjects
        try:
            if variant in ['task_environment','region_and_task']:
                session.BeginTaskEnvironment()
                began_task = True
            sketch.Activate(NXOpen.Sketch.ViewReorient.FalseValue)
            if variant in ['find_movable','find_issues']:
                discover = part.Sketches.CreateFindMovableObjectsBuilder() if variant == 'find_movable' else part.Sketches.CreateFindIssuesBuilder()
                try:
                    row['find_builder_validate'] = discover.Validate()
                    row['find_builder_commit'] = str(discover.Commit())
                finally:
                    discover.Destroy()
            if variant.startswith('show_movable'):
                session.Preferences.Sketch.FindMovableObjects = True
            if variant in ['no_update','region_and_task','show_movable_region']:
                region = part.Sketches.CreateWorkRegionBuilder()
                try:
                    region.Scope = NXOpen.SketchWorkRegionBuilder.ScopeType.EntireSketch
                    region.Commit()
                finally:
                    region.Destroy()
            if variant == 'local_update':
                sketch.LocalUpdate()
            elif variant == 'same_expression_update':
                for constraint in sketch.GetAllConstraintsOfType(NXOpen.Sketch.ConstraintClass.Any,NXOpen.Sketch.ConstraintType.NoCon):
                    if isinstance(constraint,NXOpen.SketchDimensionalConstraint) and constraint.AssociatedExpression is not None and constraint.DimensionState == NXOpen.SketchDimensionalConstraint.DimensionStateType.Driving:
                        part.Expressions.Edit(constraint.AssociatedExpression,constraint.AssociatedExpression.RightHandSide)
                        break
                sketch.Update()
            sketch.CalculateStatus()
            row['active_after_calculate'] = read(sketch)
            row['preference_before'] = preference_before
            row['preference_during'] = session.Preferences.Sketch.FindMovableObjects
            session.Preferences.Sketch.FindMovableObjects = preference_before
            row['active_after_preference_restore'] = read(sketch)
            row['preference_after_restore'] = session.Preferences.Sketch.FindMovableObjects
            row['active_task_environment'] = session.IsInTaskEnvironment
            sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue,NXOpen.Sketch.UpdateLevel.Model)
            session.UpdateManager.DoUpdate(mark)
            row['after_deactivate'] = read(sketch)
        except Exception:
            row['error'] = traceback.format_exc()
        finally:
            if session.ActiveSketch is not None:
                sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue,NXOpen.Sketch.UpdateLevel.Model)
            session.Preferences.Sketch.FindMovableObjects = preference_before
            row['find_movable_preference_restored'] = session.Preferences.Sketch.FindMovableObjects == preference_before
            if began_task:
                session.EndTaskEnvironment()
            session.UndoToMark(mark,None)
            session.DeleteUndoMark(mark,None)
            row['discovery_undo_returned'] = True
except Exception:
    report['error'] = traceback.format_exc()
finally:
    output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
