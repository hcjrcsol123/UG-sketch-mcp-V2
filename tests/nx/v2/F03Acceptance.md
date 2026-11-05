# F03 datum-plane offset acceptance

## Objective and traceability

This is real-NX subsystem validation for F03. It exercises `M2OpenModelAdapter` through the approved datum-plane prepare/execute/verify path and independently reads the resulting NX features. It covers datum-plane and explicitly identified tilted planar-face references, signed offsets, reference associativity, rejection behavior, save/reopen, supported-sketch association, parent edits, revision invalidation, and undo recovery.

The test maps to `docs/V2.md` / F03 (datum-plane offset) and the F03 request contract: `distance_mm` plus exactly one of `support_datum_plane_id` or `support_face_id`. F01/F02 creation remains a prerequisite path. This harness does not implement or validate F04.

## Test layer, setup, and controls

- **Test layer:** real NXOpen subsystem validation against the production M2 bridge and production `M2OpenModelAdapter`; it is not a simulation or source-only check.
- **Fixture:** a new, uniquely named millimeter part in `workspace/v2/models`. It contains a tilted solid cylinder for an analytic, non-axis-aligned planar cap and a native F02 datum plane. The face is selected by the production planar-face query and then passed back by its returned `face_journal_id`; the test records the identifier and independently confirms the face is planar and tilted.
- **Isolation:** the source part and save/reopen copy use unique run IDs. Existing models are not reused. The harness records the original Work Part and its modified state, requires an idle NX session outside a task environment, and must be run only by the designated NX operator. It does not start or close NX.
- **Safety controls:** no machine motion, material removal, external actuator, or physical load is involved. The journal creates and edits only the isolated CAD part. It saves before adapter takeover, uses an NX undo mark for the parent edit, and records cleanup/restoration state.
- **Instrumentation:** direct `NXOpen` reads of feature IDs, datum-plane origin/normal, `Plane.Method`, generic associative `Plane.Expression.Value`/`RightHandSide`/`Units`, raw `Plane.Flip`/`Plane.GetReverseSide()` state, `Plane.GetGeometry()`, feature `GetParents()`/`GetChildren()`, supported sketch parents, plus UF `AskFaceData` for independent face classification and normal. The expression is independently converted to mm while its raw signed value is preserved. Adapter result dictionaries are recorded separately and are not used as the only pass evidence.
- **Calibration and units:** no physical measuring instrument is used; all linear inputs/readbacks are in part units (mm). The geometry tolerance is 1e-6 mm and direction component tolerance is 1e-7. These are acceptance tolerances for NX model data, not claims about manufacturing accuracy or instrument calibration. The JSON report stores raw values and tolerances.

## Cases and procedure

The journal creates a fresh part, saves it, takes it over through `M2OpenModelAdapter`, and records assembly paths/versions, NX session state, the unique part paths, and the starting model revision. It then:

1. Creates the F02 parent datum plane through the production adapter and captures its native feature/entity IDs and independent frame.
2. Queries planar faces on the tilted cylinder, selects exactly one cap whose normalized normal has three nonzero components, and records its explicit face ID, owning body/feature IDs, plane point, and normal from UF.
3. For each reference kind (datum plane and tilted planar face), prepares and applies offsets of +12.5 mm, -7.25 mm, and 0 mm. Before each prepare, it snapshots the adapter's part definition and direct NX feature/sketch/body identifiers; after prepare it requires the snapshots and binding revision to remain unchanged.
4. After each application, independently resolves the created native datum-plane feature. It compares the datum-referenced plane origin with `reference_point + distance_mm * reference_normal`; for a face reference it checks the signed projection `dot(child_origin - face_plane_point, face_normal)` so an arbitrary in-plane display-origin choice does not cause a false failure. It compares the normalized child normal to the reference normal, requires `Plane.Method == Distance`, and reads the signed expression from `Plane.Expression.Value` (including `RightHandSide` and `Units`). The signed expression value and geometric signed projection must independently equal the request. `Plane.Flip` and `Plane.GetReverseSide()` are retained as raw evidence and are not used to adjust the expression sign. It also checks that `Plane.GetGeometry()` resolves to the requested native reference object and that the offset feature's native parent list contains the datum feature or face-owning feature as applicable.
5. Sends rejected requests for an unknown ID, a nonplanar face ID, and conflicting datum-plane/face references. Each rejected prepare must leave feature/body/sketch IDs, definition snapshot, modified state, and binding revision unchanged. The harness reports the returned error code/message; acceptance requires a clean rejection, regardless of the implementation's specific validation code.
6. Creates a sketch supported by a generated offset datum plane, saves a separate copy through production `SaveCopy(..., reopen: true)`, and independently re-resolves the offset datum and sketch after reopening. It checks the native parent dependency and the saved offset definition/readbacks.
7. Retains an unapplied stale offset plan and an applied offset plan on the native parent datum plane. Under an NX undo mark, it moves the parent datum plane by 5 mm along its own normal and runs NX update. The child offset must move by the same vector while keeping its requested signed spacing and normal. The binding revision must change, execution of the old plan must return `UNCHANGED`, and verification of the old applied plan must fail. After undo, the parent and child native geometry, native association, original revision semantics, sketch support, and old applied-plan verification must recover.

The exact feature `JournalIdentifier`s, case input, request/plan/execute/verify records, native readbacks, before/after prepare snapshots, revisions, and exception details are persisted in a uniquely named JSON report under `workspace/v2/evidence`.

## Pass/fail criteria and evidence quality

Each positive offset case passes only if production execution is `APPLIED`, production verification succeeds, native signed distance and normal meet the tolerances, the native method and signed `Plane.Expression.Value` preserve the requested offset, and both `Geometry` and `GetParents()` establish the requested reference association. Raw `Plane.Flip` and reverse-side state are reported for diagnosis and do not change the expression sign criterion. The datum-plane reference also checks the full origin; face references use the signed plane projection because NX may choose a different in-plane origin. Zero offset is checked with the same path and must retain an associative reference. The parent-edit case passes only if the child follows the parent by the measured parent delta, the stale plan/verification are rejected, and undo restores independently measured geometry and verification.

Each rejection case passes only when prepare rejects the request and produces no model or revision change. Any unrecognized/ambiguous reference accepted by prepare is a failure. A failure record includes the test case name, concrete reproduction steps, observed exception/result, captured configuration and measurements, and a retest criterion: fix the reported behavior and rerun the complete acceptance against a new unique part path; all six signed-offset cases, rejection cases, save/reopen checks, and edit/undo checks must pass.

The run uses one isolated CAD configuration and one fixture instance. The six signed-offset cases are distinct functional scenarios, not a statistical sample. A passing run does not establish reliability, cross-version compatibility, physical manufacturing accuracy, or compliance with an external standard. Any anomalous NX value is retained in the raw JSON; it is not discarded or averaged away.

## Build and execution

The build is offline and compiles the acceptance DLL and NX journal wrapper only; it does not start NX. The root project and production M2 assemblies must already be present.

```powershell
powershell -ExecutionPolicy Bypass -File tests/nx/v2/build-f03-acceptance.ps1
```

Only the designated NX operator may execute the journal in the configured idle NX session:

```powershell
& 'C:\0sf\ug\NXBIN\run_journal.exe' -nx 'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp\tests\nx\v2\RunF03Acceptance.py'
```

The output JSON is the test report. Model and report names include the run ID; no pre-existing model path is overwritten.
