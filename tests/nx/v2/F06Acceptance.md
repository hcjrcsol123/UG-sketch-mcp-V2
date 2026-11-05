# F06 NX Acceptance

## Objective and scope

This acceptance validates F06 sketch geometry through the production `M2OpenModelAdapter` in a real NX session. It exercises the existing line/circle geometry plus circular arcs, full ellipses, positive and negative elliptical arcs, and cubic interpolation splines. It also checks native profile classification for empty and open sketches, complementary circular and elliptical half-arcs, duplicate and partially overlapping arcs, a mixed arc/line closed contour, a concave contour, multiple disjoint regions with an inner loop, and a self-intersecting contour.

The harness also checks external geometry-edit revision invalidation, undo recovery, and SaveCopy/reopen readback. F12 extrusion acceptance is separate in `docs/F12.md`; this F06 run makes no claim about expanded profile extrusion.

This is functional subsystem validation for the recorded NX build and production adapter configuration. One run does not establish reliability, statistical confidence, compliance, physical accuracy, or behavior across other NX releases and part conditions.

## Traceability

| Requirement | Evidence case |
| --- | --- |
| Empty sketch remains successful and has no valid-area claim | `F06_empty_sketch_is_not_a_valid_area` |
| Existing open line remains creatable without claiming a closed area | `F06_legacy_open_line_remains_successful` |
| Signed circular arcs, including crossing zero and negative long sweep | `F06_native_curves_on_tilted_frame` |
| Full rotated ellipse and positive/negative elliptical arcs | `F06_native_curves_on_tilted_frame` |
| Ellipse schema parameter `t` is checked against UF evaluator limits and direct native points; inspect reports `t` separately from native polar angles | `F06_native_curves_on_tilted_frame`, `F06_complementary_arcs_with_tangent_shared_endpoints` |
| Cubic spline interpolation points, chord-length parameters, degree, native knots and pole data | `F06_native_curves_on_tilted_frame` |
| Spline slope/curvature condition codes and solver-generated vector readbacks | `F06_native_curves_on_tilted_frame` |
| Tilted sketch frame and world-coordinate transformations | `F06_native_curves_on_tilted_frame`, all direct NX geometry readbacks |
| Mixed non-rectangular closed loop | `F06_mixed_nonrectangular_closed_loop` |
| Complementary half-arcs close one circular and one rotated elliptical profile | `F06_complementary_arcs_with_tangent_shared_endpoints` |
| Coincident and partially overlapping arcs produce real production rollback evidence | `F06_duplicate_and_partial_arc_overlap_roll_back` |
| Concave closed profile | `F06_concave_closed_profile` |
| Inner loop and multiple disjoint regions | `F06_inner_loop_and_multiple_disjoint_regions` |
| Duplicate IDs, invalid radii/angles, and degenerate spline data reject without prepare side effects | `F06_invalid_geometry_requests_are_read_only` |
| Self-intersection is rejected or explicitly invalid and rolled back | `F06_self_intersection_is_rejected_and_rolled_back` |
| External edit invalidates old plans and verification; Undo recovers | `F06_external_geometry_edit_invalidates_and_undo_recovers` |
| All accepted curves survive production save/reopen and direct native reread | `F06_save_reopen_native_geometry_roundtrip` |

## Test classification, setup, and safety controls

The build is offline compile verification. The journal is real NX subsystem validation of the production adapter and native feature factory; it does not use a simulated model or fake adapter. Before journal execution, the production M2 assemblies must be built using the repository's normal process. Compile the acceptance and journal wrapper with:

```powershell
& 'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp\tests\nx\v2\build-f06-acceptance.ps1'
```

Only the designated NX operator runs the journal in the existing idle NX session:

```powershell
& 'C:\0sf\ug\NXBIN\run_journal.exe' 'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp\tests\nx\v2\RunF06Acceptance.py'
```

The harness requires NX outside a task environment and an empty or saved, unmodified original Work Part. It creates a uniquely named millimeter test part and an independent SaveCopy target under `workspace/v2/models`; JSON evidence is written under `workspace/v2/evidence`. It does not start or stop NX, replace production DLLs, or modify the original Work Part. At exit it releases its adapter binding, closes only the uniquely named test parts, and restores the original Work Part/session state. A restore failure fails the run and is retained in the report.

The fixture frame is intentionally spatial: origin `[10,20,30] mm`, normal `[1,2,3]`, and requested X direction `[1,-1,0.5]`. The harness independently normalizes the normal, projects and normalizes X into the plane, and calculates `Y = normal × X`. Geometry is specified in sketch-local millimeters and transformed independently into world coordinates for readback comparison.

Legacy empty/open sketches leave `constraint_mode` omitted and exercise the existing `legacy_driving` default. F06 mixed curves, non-rectangular profiles, rollback contours, region profiles, and the external spline-edit anchor explicitly use `constraint_mode=geometry_only`, because these requests verify native curve/profile behavior without applying the legacy per-primitive driving dimensions. The installed MCP smoke follows the same split and checks that the mode remains optional; the standalone legacy rectangle/circle cases omit it.

## Instrumentation, units, and uncertainty

The harness records production prepare/execute/verify results and direct `NXOpen.Sketch.GetAllGeometry()` objects. Lines, circular arcs, circles, and ellipses are read directly from native NXOpen entities. Circular-arc samples use the native arc parameter. Ellipse samples use `UF.Eval.AskLimits` and `UF.Curve.EvaluateCurve` at 25%, 50%, and 75% of the measured native parameter interval. Their expected points are calculated independently from the request's ellipse parameter `t` in `x=a*cos(t), y=b*sin(t)` and the requested rotation. `Ellipse.StartAngle` and `Ellipse.GetParameters` are retained as raw native polar-angle evidence and are never used as expected `t` values. Endpoints and signed parameter spans are checked separately, so a wrong side of an elliptical arc cannot pass by matching only its endpoints.

Splines are read through `UFSession.Curve.AskSplineThruPts` and `AskSplineData`. The harness records native interpolation points, parameters, every point's slope/curvature condition codes and numeric vectors, order, degree, poles, knots, periodicity, and parameter range before checking the condition codes. It independently checks each evaluated spline point at its UF interpolation parameter against the requested world point. It also checks cumulative-chord-length normalized parameters, cubic degree, non-periodicity, `NONE` slope codes at interior points, `NONE` curvature codes at all points, a finite nondecreasing knot vector, and a native degree/pole definition. The schema requests no user-defined endpoint tangent or curvature. On NX2506, `UF_CURVE_ASK_SPLINE_THRU_PTS` reports `AUTO` slope codes at both endpoints after adding the UF-created spline to a sketch and updating it, while the supplied middle points remain `NONE`; the header defines `AUTO` as internally calculated from the data points. The acceptance permits endpoint `AUTO` or `NONE`, rejects vector/direction controls and interior `AUTO`, and retains the generated numeric endpoint vectors as readback evidence rather than treating them as explicit constraints.

Profile classification is taken from the production execute and live verify results. Both records must contain matching `profile_analysis` values. The acceptance checks `status`, curve/open-chain/closed-loop/outer-loop/inner-loop counts, section-loop count, native intersection status, and spline self-intersection status. An unavailable or failed check is a failure for the closed-profile scenarios. Empty and open sketches must report `EMPTY` and `OPEN_GEOMETRY` respectively and are not evidence of a solid area.

All positions and radii are millimeters. Native circular-arc and UF ellipse evaluator parameters are radians. Raw ellipse polar angles are separately identified in radians. Limits are `1e-6 mm` for point/radius values, `1e-7` for unit frame components, and `1e-7 rad` for native angular spans. No external metrology instrument is used, so there is no instrument calibration; these are NX model comparison tolerances and do not imply manufacturing accuracy. The run records NXOpen and UF assembly versions and paths. A single run is one observation per case and is not a reliability sample.

## Procedure and pass/fail criteria

1. Create an empty sketch and require APPLIED execution, successful verification, zero native curves, and `profile_analysis.status=EMPTY` with all loop counts zero.
2. Create one legacy open line and require exact direct world endpoints plus `OPEN_GEOMETRY`, one open chain, and no closed loops.
3. In one tilted-frame `geometry_only` sketch create a full circle, a positive arc crossing 0 degrees, a negative arc with a 225 degree sweep, a rotated ellipse at a nonzero center, positive and negative elliptical arcs, and a five-point spline. Require exact curve count and expected native NX object classes; compare direct centers, radii, major-axis direction, normals, arc endpoints and interior samples; directly read and evaluate all spline points and UF definition data, retaining every native endpoint/interior slope and curvature code/vector before assertions.
4. Create a closed D-shaped contour from a circular arc and chord. Require exactly two native curves, no open chains, one closed outer loop, no inner loop, verified native intersection checks, successful section classification, and `VALID_CLOSED_PROFILES` in both execute and verify results.
5. Create complementary circular and rotated elliptical half-arcs. Require each pair to form one closed outer loop and keep native intersection and section checks verified.
6. Execute duplicate and partially overlapping circular-arc requests on fresh `geometry_only` plans. Require `ROLLED_BACK`, `M3_OPERATION_ROLLED_BACK`, `profile_analysis.status=INVALID_SELF_INTERSECTION`, verified native intersection analysis, production `undo_called`, `semantic_restored`, `geometry_restored`, `history_restored`, and `bodies_restored` evidence, zero Undo update errors, preserved native failure text, and exact feature/sketch/body/curve/modified/active-sketch baselines before any harness cleanup Undo. Any discrete-intersection count is retained as evidence, but cannot override the overlap classification.
7. Create a six-edge concave L contour. Require direct line endpoint agreement and one valid closed outer loop.
8. Create a concave outer contour, one contained inner loop, and a separate triangle. Require 13 native lines, three closed loops, two outer loops, and one inner loop.
9. Prepare malformed inputs separately: duplicate IDs, zero arc radius, zero and full-turn partial sweeps, out-of-range start/rotation angles, reversed ellipse radii, repeated spline points, and fewer than four spline points. Every request must return `M2ModelException(INVALID_REQUEST)` while feature/sketch/body IDs, modified state, and binding revision remain unchanged.
10. Submit a bow-tie closed line contour. The adapter may reject it during prepare or execute; if an execute result exists, it may never report `VALID_CLOSED_PROFILES`. Failed-execution rollback must be directly visible in feature/sketch/body and curve inventories before a test cleanup Undo is considered.
11. Apply and verify a fresh `geometry_only` sketch containing one cubic spline; retain an unexecuted legacy line-sketch plan; then use `UF.Curve.EditSplineThruPts` to move one middle interpolation point 2 mm along the sketch Y axis under an NX Undo mark. Run `Session.UpdateManager.DoUpdate` and require zero update errors. Require its Tag, JournalIdentifier, curve count, curve-ID list, and feature/sketch/body inventory to stay fixed while the evaluated native shape, direct poles/knots snapshot, and nested `InspectSketch.geometry[].nurbs_definition` record change. Require a changed binding revision, `UNCHANGED` execution for the stale plan, failure of the old spline verification while edited, and successful verification after Undo restores the original NURBS data.
12. Save a unique production copy with `reopen=true`. Require `SAVED`, `reopened=true`, all SaveCopy checks `VERIFIED`, stable sketch JournalIdentifiers, and direct NXOpen/UF reread of every accepted sketch and curve after reopen.

The harness starts the evidence report with `passed=false`. It does not convert failures into passes. Each failed case records objective, reproduction steps, observed exception/result, and a retest criterion. Retest after correcting the implementation or harness by rebuilding production assemblies and the acceptance DLL, then rerunning all cases on a new unique model path. Preserve the original failed JSON and model artifacts while investigating anomalous results.

## Reporting and result quality

The JSON report is named `workspace/v2/evidence/F06-acceptance-<run-id>.json`. It records case inputs, expected and measured values, direct native curve identities/types, profile analysis, adapter and NX assembly configuration, source and roundtrip model paths, errors, defects, and session restoration. Each curve list is compared to the requested count and matched one-to-one, which detects missing or extra native curves. The report keeps the original exception and native readback for anomalous values. Each offline compile has a separate evidence text file named `workspace/v2/evidence/f06-acceptance-build-<n>.txt`; it does not claim an NX runtime result.

## Completed verification evidence

The final build5 package compiled four production assemblies with zero warnings. The full M2 software suite passed 114/114 tests, the independent unit suite passed 26/26, and regressions passed for F01/F02 11/11, F03 14/14, F04 26/26, and F05 32/32. The installed application reports version `0.6.0` and exposes 21 tools. Consolidated results are in `workspace/v2/evidence/f06-final-summary.json`.

Real NX2506 subsystem validation passed all 13 F06 cases with zero defects in `workspace/v2/evidence/F06-acceptance-ce8272fbeed747fbb75931e89b991704.json`. NXOpen `2506.0.0.0` and UF `2506.1700.0.0` were loaded. The run includes native geometry, profile classification, production rollback/readback, same-Tag/JournalIdentifier spline editing, revision invalidation, stale-plan rejection, Undo recovery, and SaveCopy/reopen. The edited spline kept its native identity and one-curve inventory; direct and inspected NURBS definitions changed; revision advanced from 10 to 11; stale execution returned `UNCHANGED`; verification failed while edited and passed after Undo. SaveCopy returned `SAVED` with `reopened=true`, all four checks `VERIFIED`, and direct readback of nine sketches.

The installed MCP smoke passed all 13 cases in `workspace/v2/evidence/f06-mcp-installed-7.json`. Ten modeling steps returned `APPLIED` and verified; eight sketches were independently read after reopen; associations, expression, geometry, and history SaveCopy checks all returned `VERIFIED`. Ownership returned to `HUMAN_OWNED` with `safe_for_human=true`, zero in-flight operations, and `writes_halted=false`.

The UNKNOWN recovery fault injection covered the CLR fake adapter. It did not deliberately damage NX Undo or Section cleanup in the real NX session. The real NX cases verify successful production rollback and restoration after expected invalid geometry and native overlap failures.

The acceptance uses deterministic fixtures and one fresh model path per execution. It supports functional claims only for the observed configuration. Repeat relevant NX versions, geometry scales, parameter ranges, orientations, and part conditions before making broader performance, reliability, compliance, or generality claims.
