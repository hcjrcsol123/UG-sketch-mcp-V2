# F01/F02 NX Acceptance

## Objective and scope

This procedure validates the production NX adapter and feature factory in an actual NX session. It covers F01 sketches on a free spatial frame, an existing native DatumPlane, and a non-axis-aligned planar Face; F02 creation of a native spatial DatumPlane; an empty sketch; invalid-frame rejection without model change; and regression for the legacy XY, XZ, and YZ modes. Geometry remains limited to the existing line, rectangle, and full-circle operations.

It is functional verification of the current NX build and adapter configuration. A single acceptance run does not establish reliability, statistical confidence, standards compliance, or correctness for every possible frame or face.

## Traceability

| Requirement | Objective evidence |
| --- | --- |
| F01: create an empty sketch at an arbitrary frame | `F01_free_frame_empty_sketch`; direct NXOpen frame readback and zero authored curves |
| F01: place supported line/circle geometry in an arbitrary spatial frame | `F01_free_frame_line_circle_world_coordinates`; independent transform checked against NXOpen endpoints, center, and radius |
| F01: attach a sketch to an existing native DatumPlane | `F01_existing_native_datum_plane_support`; production support association and native feature parent checked |
| F01: attach a sketch to a tilted planar Face | `F01_tilted_non_axis_face_support`; all-planar query, explicit face JournalIdentifier, direct UF face readback, native support check |
| F02: create a native spatial DatumPlane | `F02_native_spatial_datum_plane`; feature JournalIdentifier, direct DatumPlane entity, vectors, inventory definition |
| Invalid frame has no side effects | `F01_invalid_frame_rejected_without_model_change`; `INVALID_REQUEST` and unchanged feature/sketch/body identifiers, revision, and modified state |
| Valid prepare is read only | Each F01 sketch case and `F02_native_spatial_datum_plane` records the inspected feature/sketch/body/definition state immediately before and after prepare |
| Existing principal-plane behavior remains usable | `F01_legacy_XY_regression`, `F01_legacy_XZ_regression`, `F01_legacy_YZ_regression` |
| Native plane and support survive save/reopen | `F01F02_saved_copy_reopen_and_association_roundtrip`; production SaveCopy checks, native plane entity, feature ancestor dependency, and InspectSketch evidence |
| Datum support changes invalidate stale plans/results | `F01_native_datum_support_revision_invalidation`; NXOpen DatumPlaneBuilder edit, adapter revision capture, stale execute/verify rejection, and undo recovery |

## Test classification and setup

The build script is offline build verification. The acceptance journal is NX subsystem validation of the production M2OpenModelAdapter/M3NxFeatureFactory path, including NXOpen feature creation, model readback, and save/reopen. It does not substitute a fake adapter or simulated NX model.

Before execution, build the production M2 assemblies with the repository's normal offline build process. Then compile the acceptance DLL and syntax-check the source journal wrapper offline:

```powershell
& 'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp\tests\nx\v2\build-acceptance.ps1'
```

In an idle NX session, load the Python journal with the local run_journal executable:

```powershell
& 'C:\0sf\ug\NXBIN\run_journal.exe' 'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp\tests\nx\v2\RunAcceptance.py'
```

The Python entry calls `Session.Execute` on the compiled `RunAcceptanceJournal.dll` and `NXJournal.Run()`. The C# wrapper then loads the compiled acceptance assembly and resolves the production M2Bridge/M2Entry and NXOpen assemblies from their configured build folders. The acceptance creates a unique millimeter part under `workspace/v2/models`, seeds a large cylinder whose cap normal is `[1,1,1]/sqrt(3)`, saves that fixture, and takes it over through the production adapter. The F02 plane uses origin `[10,20,30] mm`, normal `[1,1,1]`, and x direction `[1,-1,0]`. The free-frame sketch also uses origin `[10,20,30] mm`, normal `[1,1,1]`, and a deliberately non-orthogonal x direction `[1,0,0]` so projection and normalization are tested.

The Python entry first creates a unique `F01F02-python-journal-start-<run-id>.json` marker. The C# entry creates a separate `F01F02-journal-start-<run-id>.json` marker when `NXJournal.Main` is entered. A successful `run_journal.exe` process exit without the C# marker and a corresponding acceptance JSON report is not acceptance evidence. The marker only proves entry execution; the acceptance report's `passed` field is the result.

The test refuses to start when NX is in a task environment or the existing Work Part is modified. The harness saves only uniquely named source/roundtrip parts beneath `workspace/v2/models`; the JSON result and compiled acceptance assembly go under `workspace/v2/evidence`. It restores the original Work Part or closes test parts and returns the session to an empty Work Part state.

## Instrumentation, units, and uncertainty

Evidence combines production adapter plan/execute/verify/inspect results with direct NXOpen reads: `Sketch.Origin`, `Sketch.Orientation`, `Sketch.GetAllGeometry()`, `Line.StartPoint/EndPoint`, `Arc.CenterPoint/Radius`, `DatumPlane.Origin/Normal/GetCornerPoints()`, `Feature.GetParents()/GetChildren()`, and UF `AskFaceData`. The test calculates the expected frame independently using vector normalization, x-axis projection, and `y = normal × x`; local points are transformed using that independently calculated basis.

Lengths and coordinates are millimeters, areas are square millimeters, and vector components are dimensionless. Pass limits are 1e-6 mm for point/radius comparisons, 1e-7 per component for normalized axes, and 1e-5 mm for the support-plane distance check. These compare NX model values; they are not physical metrology. No external measuring instrument is used, so no instrument calibration applies. Record NX build and NXOpen assembly details returned by the production adapter. The limits are numerical acceptance tolerances, not claims about manufacturing accuracy.

## Procedure and pass/fail criteria

The harness executes each case once against a fresh uniquely named part and records case inputs, returned plans, execution/verification results, raw NXOpen values, and exceptions.

1. Prepare an invalid frame whose x direction is parallel to its normal. Pass only when prepare returns `M2ModelException.Code == INVALID_REQUEST` and feature/sketch/body identifiers, binding revision, and part modified state are unchanged.
2. Create the F02 DatumPlane at the spatial coordinates above. Pass only when execution is `APPLIED`, verification and all reported checks pass, the `datum_plane_id` equals the native feature JournalIdentifier, the feature inventory contains its datum-plane definition, and direct NXOpen origin/normal/x/y values meet tolerance with a right-handed frame.
3. Create and verify an empty F01 free-frame sketch. Pass when it has the requested frame and no NXOpen curve objects; the NX empty/underconstrained status by itself is not a failure.
4. Create a free-frame line from local `(2,3)` to `(8,3)` and a full circle centered at `(5,-4)` with 6 mm diameter. Pass when raw NXOpen coordinates match the independent world transform and all production verification checks pass.
5. Create a line/circle sketch on the F02 DatumPlane using its `SupportDatumPlaneId` and x direction. The origin and normal come from the native plane by default. Pass when InspectSketch reports the same datum-plane support, the native GetParents dependency chain reaches the datum feature through NX internal sketch features, and independent geometry checks pass.
6. Query all planar faces on the cylinder fixture and choose its supported cap with a non-axis-aligned normal. Use the returned `face_journal_id`, an interior cap centroid, and the x direction. Pass when UF reads a planar face, the frame remains in the face plane, production verification reports native FACE support with the same support face ID, and the rectangle's NXOpen geometry matches the requested local transform.
7. Create line sketches using each legacy XY/XZ/YZ selector. Pass when each plan is applied and verified and direct NXOpen frame/line values retain the documented principal-plane orientation.
8. Save a new copy through production `SaveCopy(..., reopen:true)`. Pass when the copy is saved and reopened, all roundtrip checks pass, the native DatumPlane feature and sketch JournalIdentifiers resolve, the native datum dependency remains reachable from the supported sketch, and both datum/face support records remain available after reopen.
9. Create and verify a fresh DatumPlane-supported anchor sketch against the current model. Move the native DatumPlane origin by 2 mm through `DatumPlaneBuilder.GetPlane()`, `Plane.SetMethod(Fixed)`, `Plane.EditFixedPlane()`, and `Plane.Evaluate()` inside a test undo mark. Pass when the adapter revision changes, the old prepared sketch plan returns `UNCHANGED`, the fresh applied plan no longer verifies while the support is changed, and NX undo restores the original native plane, parent link, and anchor plan verification.
10. Pass the run only when every case passes and the original NX session state is restored.

Any failed assertion, unexpected adapter error, missing feature/association, result outside tolerance, failed roundtrip, or failed session restoration is a failure. The JSON report starts with `passed:false`; a failure entry retains the original exception text, the reproduction steps, and a retest criterion. Do not edit a failed report into a passing result. Fix the implementation, rebuild the production assemblies and acceptance DLL, then rerun the failed case and the complete regression sequence on a new model path.

This entry does not inject an unpredictable NX feature-create/commit failure. Native feature creation rollback is therefore not independently validated by this procedure; the invalid-frame test covers prepare-time no-change behavior, and protocol/undo uncertainty cases remain separate evidence.

## Sample size and reporting

The current run has one fresh part per execution and one observation per scenario. Its deterministic geometry and association checks establish the recorded configuration's functional behavior only. Repeat across relevant NX builds, face orientations, dimensions, and part conditions before making broader performance or reliability claims.

Each result is written as `workspace/v2/evidence/F01F02-acceptance-<run-id>.json`. The JSON includes run/configuration identity, model paths, case objectives and input steps, returned adapter data, direct native measurements, checks, pass state, exceptions, defects, reproduction steps, retest criteria, and session restoration. The run also records the unique source and SaveCopy target paths; preserve those models with the evidence when investigating a failure.
