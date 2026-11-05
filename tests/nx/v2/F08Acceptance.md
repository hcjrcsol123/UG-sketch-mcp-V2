# F08 native sketch offset acceptance

## Objective and boundaries

Demonstrate that the frozen F08 interface offsets an explicitly ordered chain
on the named native sketch, uses traversal-relative `left` / `right` correctly,
keeps source curves when `retain_original=true`, joins output curves as requested,
and verifies the resulting geometry independently. Acceptance is for the
recorded NX2506 and millimetre configuration only. One successful run does not
establish reliability, manufacturing accuracy, regulatory compliance, or
behavior in another NX release.

Traceability: `docs/V2.md` F08/T09, `docs/V2.md`
F08 continuation boundary, and the F08 interface in the current production
contract. The software layer is `tests/m2/test_f08_sketch_offset_protocol.py`
and `tests/m2/test_f08_sketch_offset_lifecycle.py`; analytic oracles are in
`tests/unit/test_f08_offset_geometry.py`.

## Configuration, setup, and safety controls

The designated NX operator starts one idle NX2506 session with millimetre part
units, no task environment, and a saved, unmodified original Work Part. The
acceptance creates a uniquely named isolated part under
`workspace/v2/models/`, opens it through production `M2OpenModelAdapter`, and
uses only that copy. It stops if the original Work Part is modified, the part
unit is not millimetres, another test model is active, a requested output path
already exists, or the adapter cannot identify the unique test part. Run cases
serially in one NX session; do not run the installed MCP smoke against the same
part concurrently. The harness does not start NX, force-close NX, clear dirty
state, overwrite a source part, or replay a request after an unknown outcome.

The operator runs the Journal entry only after the acceptance assembly is
built and the target isolated test part is ready. `M2OpenModelAdapter` owns the
prepare/execute/verify path and NX Undo recovery. Each case records its request,
native pre-state, production operation result, production verification, direct
NXOpen post-state, and any cleanup/rollback evidence. Failure artifacts remain
available for reproduction; the runner never reports a pass if recovery is
UNKNOWN.

No physical instrument is used. Geometry is read from native NXOpen curve
objects and the production inspection API; production response text alone is
not an oracle. Instrument calibration is not applicable. The source and output
part are millimetre units. Use 1e-6 mm for point, radius, and length comparisons
and 1e-7 rad for frame / tangent direction comparisons, matching the recorded
CAD numeric acceptance tolerance. Closed-loop status is independently derived
from native curve endpoints and their connected components, with each intended
endpoint pair meeting within 1e-6 mm. Record actual NX tolerances if NX reports a
looser modeling tolerance; do not silently relax a failing comparison.

## Test objectives and cases

Every passing edit must show an `APPLIED` mutation and a separate successful
verify. The verify result must include geometry, closure / connectivity,
sketch ownership, constraints, non-target geometry, history, solver state, and
body preservation where applicable. The harness then independently enumerates
the native Sketch geometry, calculates expected locations and dimensions, and
compares the complete pre/post source and non-target records.

| Case | Setup and procedure | Objective pass evidence |
| --- | --- | --- |
| F08-01 line, both sides | Create one 10 mm horizontal Line from (0,0) to (10,0). Prepare and execute distance 2 mm for each side in a separate sketch. | Forward `left` has support at y=+2 mm; `right` at y=-2 mm. The added Line remains parallel and exactly 2 mm from the source. Source endpoints remain present. |
| F08-02 open L chain | Create (0,0)→(10,0) and (10,0)→(10,10), chain in order, distance 2 mm, `join_mode=extension`; run once per side. | `left` support lines are y=2 and x=8, joined at (8,2); `right` lines are y=-2 and x=12, joined at (12,-2). The output chain is connected and open at the two expected remote endpoints. |
| F08-03 direction reversal | Use the same L geometry but chain top-to-bottom and then corner-to-left, setting both entries to `reverse`; request `left`, distance 2 mm. | Native geometry coincides with F08-02 `right` within tolerance. This explicitly proves `left` is relative to the supplied traversal direction. |
| F08-04 closed square inside/outside | Create a CCW 10×10 mm four-Line loop from (0,0); offset distance 2 mm with both side values in separate operations and `join_mode=extension`. | `left` loop has bounds [2,2] to [8,8], area 36 mm², perimeter 24 mm; `right` has bounds [-2,-2] to [12,12], area 196 mm², perimeter 56 mm. Native connectivity is closed, and all four source Lines remain unchanged. |
| F08-05 `join_mode=arc` outer corner | Create the open L chain from F08-02 and request distance 2 mm, `side=right`, `join_mode=arc`. Repeat in a fresh Sketch with reversed chain order/directions and `side=left`. Read native Arc center, endpoints, start/end angle, and sweep sign. | Forward chain lines are (0,-2)→(10,-2) and (12,0)→(12,10); the connector is a CCW quarter Arc centered at (10,0), radius 2 mm, from angle -90° to 0°, sweep +90°, and length π mm. Reversed traversal produces the same spatial geometry with reversed Arc endpoint order and -90° sweep. Arc tangents align with both adjoining Lines; each output chain is connected. |
| F08-06 mixed per-curve direction | Store the first L Line left-to-right and the second Line top-to-bottom. Submit chain entries `[first:forward, second:reverse]`, distance 2 mm, `side=left`, `join_mode=extension`. | Output supports are y=2 and x=8 and meet at (8,2); output is connected and open. This proves direction is applied per chain entry even when source object parameter directions differ. |
| F08-07 connected Line/Arc/Line chain | Create Line (-10,0)→(0,0), a radius-5 mm Arc centered at (0,5) from 270° to 360°, then Line (5,5)→(5,15). Offset 2 mm with `join_mode=extension` on each side. | `right` has supports y=-2, radius 7 mm, and x=7; `left` has y=2, radius 3 mm, and x=3. Endpoints connect in chain order, Arc sweep/center/radius stay correct, source three curves remain unchanged, and output is an open connected chain. |
| F08-08 closed Line/Arc profile | If the frozen production capability includes closed mixed chains, create a CCW capsule with two 20 mm straight spans and two radius-5 mm semicircles; offset distance 2 mm on both sides using extension joins. | Inner side has radius 3 mm, straight support y=±3 mm, perimeter 40+6π mm, and area 120+9π mm²; outer side has radius 7 mm, y=±7 mm, perimeter 40+14π mm, and area 280+49π mm². Both outputs are closed and all original Lines/Arcs remain unchanged. |
| F08-09 full Circle capability boundary | Current production capability advertises `LINE` and `ARC`. Submit one full Circle as a chain entry and snapshot the model. If the final contract continues to omit Circle, require a specific read-only unsupported-type rejection. If Circle support is explicitly added, run a radius-5 mm Circle at distance 2 mm on both sides and record its native orientation. | Unsupported Circle leaves the native snapshot/revision unchanged. If supported, CCW traversal left / right radii are 3 / 7 mm; reverse traversal swaps them, center is unchanged, full-curve closure remains true, and source Circle is unchanged. |
| F08-10 tilted plane | Repeat F08-02 in a sketch with origin (10,20,30) mm, normal (1,2,3), and requested X axis (1,-1,0.5). Independently transform local endpoints and join into world coordinates. | Native endpoints match independently transformed world points within 1e-6 mm; output remains in the sketch plane and 2 mm from each selected support. It must not silently use world XY. |
| F08-11 preservation | Include an unselected reference Line, a selected connected pair carrying at least one nonempty Driving dimension and one geometric Coincident relation, plus a second Sketch with its own nonempty constraints. Capture every curve/reference status, dimension RHS/unit/state/operands, geometric relation operands, feature/history, body inventory, solver status, and part identity. | Source geometry and reference status, all existing target and other-Sketch constraints, history, solver state, bodies and identity match the pre-edit snapshot. Only intended offset geometry is added; new geometry is active rather than reference. `retain_original=false` rejects before native mutation. |
| F08-12 read-only rejection | Independently submit missing/cross-Sketch curve IDs, unsupported object type, zero/negative/non-finite distance, invalid or empty chain/direction, and a disconnected directed chain. Snapshot native model, revision, and dirty state around each prepare. | Each deterministically invalid request returns a specific rejection before a native offset call; native geometry, references, constraints, history, bodies, and revision remain unchanged. Do not infer a rejection from a swallowed exception. |
| F08-13 degenerate native arc result | Use a fresh right-angle L chain, `distance_mm=2`, `side=left`, `join_mode=arc`. NX2506 was observed to return an additional radius-zero Arc for this inside corner. Capture pre-state, prepare once, and execute at most once if a plan is returned. | Accept either a read-only prepare rejection with no native call and exact unchanged state, or a post-call failure with actual NX Undo, `ROLLED_BACK`, and an exact native snapshot restoration. A successful/APPLIED result containing a radius-zero Arc is a failure. This case is distinct from preflight parameter/chain validation. |
| F08-14 native failure recovery | If F08-13 is rejected before execution, use the predeclared inner-offset/collapse fixture where native `EvaluateOffset` reports an invalid result but prepare does not certify it; capture complete pre-state and execute once. | At least one deterministic invalid native postcondition reaches execution and is rolled back with curves/reference states, both sketches' constraints, solver state, history, bodies, revision/dirty state restored. If recovery is uncertain, only `UNKNOWN` with writes halted is safe and the acceptance records a defect. No automatic retry is allowed. |
| F08-15 stale plan and independent verify | Prepare a valid preview, invalidate its revision with one controlled edit on another Sketch, then execute the stale plan. Separately execute a fresh plan, call production verify, and read native geometry independently. | Stale plan rejects without a native call or geometry change. Fresh operation is APPLIED once; independent verify performs no mutation and all coordinates, connectivity, retained-source and reference-status records match. |
| F08-16 save/reopen | Save Copy to a unique `-roundtrip.prt`, reopen, query current native sketch IDs, and re-read original and offset geometry, references, constraints, and closure. | Save Copy reports saved and reopened; every production round-trip check is VERIFIED; independent NXOpen readback still matches numerical oracles and the output curves remain active. The source model path is not overwritten. |

The closed mixed-profile and Circle rows are conditional only on the frozen
support contract and the native API probe. They are required when production
advertises those input types. A capability not advertised must be rejected
before mutation and recorded as unsupported, not counted as a successful
geometry test. The open Line/Arc/Line row and both join modes remain required
because the native probe has produced a usable mixed-chain case and an observed
invalid inner Arc case.

## Reporting, failure reproduction, and retest

The Journal runner writes one JSON acceptance report and one isolated source
model plus one save/reopen model per run. The report records: run and case IDs;
UTC timestamps; source, round-trip, and report paths; NX and assembly versions;
part units; adapter binding/revision; tool/request fields; source chain order and
directions; join/side/distance; stated tolerances; sample size; direct native
before/after snapshots; production result/verification; native operation/Undo
state; save/reopen checks; and pass/fail status. `sample_size=1` for the native
configuration run; no reliability inference is made. No hash is required or
calculated.

Each failure record contains the smallest reproducible case setup, exact tool
request or adapter call, observed native coordinates/state, expected numeric
coordinates/state, NX exception or error code, Undo result, and a retest
criterion. Retest uses a fresh uniquely named part and report path, repeats the
failed case and preservation snapshot, and requires the exact failed check plus
the affected save/reopen check to pass at the recorded tolerance. Keep the
original failure report and model.

Separate report sections distinguish Python protocol / CLR fake tests,
independent analytic oracles, Journal subsystem validation through production
`M2OpenModelAdapter`, and installed MCP smoke. Compilation or fixture creation
does not count as native acceptance. Do not add broad F01–F07 regression runs
to this acceptance entry.
