# F04 angle datum plane acceptance

## Objective and traceability

This harness validates F04 through the production `M2OpenModelAdapter` in a real NX session. It checks the angle request and plan, native `Plane.Method`, angle expression and units, right-hand geometry, support and axis associations, rejection behavior, angle-plane sketch support, save/reopen persistence, revision invalidation, and Undo recovery.

Traceability is to `docs/V2.md` / T04 and the F04 request contract: `Mode="angle"`, a finite `AngleDeg` in the inclusive range -180 to +180 degrees, `AxisLineId` resolving to a native `NXOpen.Line`, and exactly one `SupportDatumPlaneId` or `SupportFaceId`. The F02 oblique datum and tilted planar face are explicit fixture references. The NX API probe found the native Plane expression convention stores `-AngleDeg`; the resulting normal is independently expected from a right-hand Rodrigues rotation about the line's `StartPoint` to `EndPoint` direction. The test checks those facts separately.

## Test layer, setup, and safety controls

- **Test layer:** NXOpen subsystem validation through the production bridge and adapter. Building the harness is an offline compile only; it is not a simulation or a test result.
- **Session control:** the journal requires an idle NX session outside a task environment and a saved, unmodified original Work Part. The harness creates a new uniquely named millimeter part under `workspace/v2/models`; it does not use the user's original part as a fixture. The harness does not start NX.
- **Fixture:** an F02 fixed datum at `[120, 230, 340] mm` with normalized normal `[1, 2, -1]`; a UF-created tilted cylinder with an explicitly selected planar cap; and raw independent `NXOpen.Line` objects for the main axis, reversed axis, off-plane rejection, normal-direction rejection, and tilted-face axis. The main axis is spatial and lies in the oblique support plane.
- **Identifier control:** create all fixture lines before reading their `JournalIdentifier`s. NX may renumber existing line identifiers when another line is inserted. The harness retains the native line objects and refreshes IDs after the sketch creates its own line; SaveCopy uses the final pre-save IDs and resolves the reopened lines again.
- **Recovery:** each parent or axis edit has a separate invisible NX undo mark. It is always undone in a `finally` path. A failed test leaves a unique report and model path for diagnosis; it does not overwrite a prior artifact.

## Instrumentation, units, and tolerances

The harness reads `DatumPlane.Origin` and `DatumPlane.Normal`, `DatumPlaneBuilder.GetPlane()`, `Plane.Method`, `Plane.Expression`, `Plane.GetGeometry()`, feature `GetParents()` ancestry, and native line endpoints. UF `AskFaceData` independently confirms face type, point, and normal. `Plane.Expression.Value` is converted from its native unit to the part's `Degrees` unit before comparison. The test also records the expression's raw value and unit symbol. Rodrigues rotation is calculated in the harness from the measured reference normal and directed native line.

The NX2506 model-value tolerances are 1e-6 mm for points and coplanarity, 1e-7 for unit-vector comparisons, and 1e-6 degrees for angle expressions. These are numeric CAD-model comparison limits, not manufacturing tolerances or physical measurement uncertainty. No external sensor is used, so no physical instrument calibration applies; the degree-unit lookup and conversion are part of each direct readback. A passing report must preserve raw values and anomalous values rather than averaging them away.

## Case matrix and procedure

The complete run contains 26 top-level cases when all prerequisites execute:

| Coverage | Cases | Evidence required |
| --- | --- | --- |
| F02 oblique reference prerequisite | 1 | Prepare snapshot unchanged; APPLIED and verified; direct origin and unit-normal readback. |
| Datum reference angles | 7 | +30, -30, 0, +90, -90, +180, -180 degrees; direct Angle method, converted expression, Rodrigues normal, native axis/reference geometry, parent ancestry, and coplanarity. |
| Axis direction and face reference | 2 | +30 about the reversed line direction; +30 from an explicit tilted planar Face and its own coplanar line. |
| Prepare rejection | 12 | Unknown axis; non-Line axis; off-plane axis; axis parallel to reference normal; unknown reference; nonplanar face; both support IDs; angle combined with nonzero distance/fixed-frame fields; below-range angle; above-range angle; NaN; positive infinity. Each compares full before/after definition and ID snapshots and binding revision. |
| Angle datum sketch support | 1 | APPLIED and verified sketch, adapter inspection support ID, and native sketch-feature ancestry to the angle datum. |
| SaveCopy/reopen | 1 | `SaveCopy(..., reopen: true)` returns SAVED with verified production roundtrip checks; independently re-read the datum-reference and face-reference angle definitions, expressions, axis/reference geometry, and ancestry after reopening; recheck the saved sketch association. |
| Reference edit, stale plan, and Undo | 1 | A fresh applied angle baseline and separate stale plan; rotate the parent datum by +10 degrees about the common in-plane axis without translating it off the axis; verify child update and revision change; stale execute is UNCHANGED; old verification fails; Undo restores geometry and verification. |
| Axis edit, stale plan, and Undo | 1 | A second fresh applied baseline and stale plan; rotate the raw line direction +10 degrees within the support plane through the reference origin; verify child update and revision change; stale execute is UNCHANGED; old verification fails; Undo restores line, child, and verification. |

For every positive datum case, the harness records request input, plan, execution, production verification, raw native values, independent expected geometry, and tolerances. It asserts `Plane.GetGeometry()` contains the exact selected axis `Line` and the selected support reference (or its native entity/owner identity), and that the datum feature's parent ancestry reaches the support owner. It intentionally does not require a raw line with no parent feature to appear in `GetParents()`; line association is established through `Plane.GetGeometry()`.

Each `PrepareDatumPlane` positive and rejection case brackets the call with direct IDs, part modified state, adapter inspection, and binding revision. Rejections pass only when prepare throws and the complete captured model state remains unchanged. A plan returned for an invalid request is recorded as a failure and is not executed.

## Pass/fail and reporting

Overall `passed` is true only when all 26 required cases have status `PASS` and the defects list is empty. Any prerequisite failure, skipped downstream case, missing native readback, mismatch, unexpected acceptance, save/reopen failure, stale-plan failure, or failed Undo recovery makes the run fail. The report includes the full case procedure, input, expected result, raw native values, tolerances, plan/execute/verify data, before/after snapshots, IDs, revisions, and exception details.

Every failed case adds a defect record with a concrete reproduction sequence, observed exception/result, and a retest criterion requiring the full acceptance on a new unique model path. Fixing a defect does not convert the original failed report to a pass; retain it and create a new run report.

JSON evidence is written to `workspace/v2/evidence/F04-acceptance-<run-id>.json`. Source and roundtrip parts use matching unique IDs under `workspace/v2/models`. Journal wrapper start/failure markers are separate files in the evidence directory. These records identify the loaded NXOpen and production assemblies so results remain tied to the tested configuration.

## Evidence limits

The matrix uses one NX2506 session and one isolated model instance. The distinct angle values and edit scenarios provide functional coverage, not a statistical reliability sample. The result does not establish cross-version compatibility, manufacturing accuracy, endurance, environmental performance, external-standard compliance, or reliability. No mechanical load, thermal, noise, or physical endurance claim is made for this CAD feature.

## Build and execution

Build offline; this command compiles the acceptance DLL and journal wrapper and does not launch NX or GUI:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File tests/nx/v2/build-f04-acceptance.ps1
```

Only the designated NX operator executes the journal in the configured idle session:

```powershell
& 'C:\0sf\ug\NXBIN\run_journal.exe' -nx 'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp\tests\nx\v2\RunF04Acceptance.py'
```

An offline successful compile is not an NX acceptance result. Leave actual `passed` status pending until the operator runs the journal and reviews the JSON report and model artifacts.
