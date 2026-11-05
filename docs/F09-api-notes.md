
# F09 NXOpen API notes

## Native engine

Linear, rectangular, and circular modes read the native Line or Arc definition and apply rigid transforms before calling `NXOpen.CurveCollection.CreateLine` or `CreateArc`, followed by `Sketch.AddGeometry(InferNoConstraints)`. Linear and rectangular offsets use normalized sketch-local directions mapped to world vectors. Circular transforms use Rodrigues rotation around the world sketch normal. An Arc copy uses the source `Arc.GetOrientation` basis rotated by the same transform; it preserves source radius and start/end parameter interval, including a full `2π` interval for circles. The part must use millimeters, as required by the F09 adapter contract.

Array output is independent sketch geometry: no associative `SketchPattern` feature, pattern constraints, or spacing expressions are created. Curves use `InferNoConstraints`. The production factory records `native_engine` as `NXOpen.CurveCollection geometry pattern`.

Mirror mode retains `SketchCollection.CreateSketchMirrorPatternBuilder(null)` with a `Section` of explicit source rules and `DirectionObject.Value` set to the selected sketch Line axis. It keeps `CreateConstraint`, `ConvertToReference`, and `ConvertInputToReference` false so the source and axis remain ordinary geometry. NX2506 exposes both SketchPatternBuilder factories, but only the mirror builder has successful native output evidence; the production array path no longer depends on SketchPatternBuilder.

## Output and preservation readback

Array modes do not call `Commit()` and return a null `CommitObject`. Mirror `Commit()` may also return null while still adding curves. For every mode the production bridge collects outputs from the target `Sketch.GetAllGeometry()` Tag delta, requires every output to be owned by the target Sketch, and checks that the global `Part.Curves` Tag delta equals the target Sketch output set. Independent verification maps each output to one requested source/position and checks source curves, constraints, expressions, reference states, other Sketches, history, bodies, and helper inventories.

Curve evidence includes native Tag, JournalIdentifier, type, coordinates, Arc basis/angles, and intrinsic length. The adapter's numeric oracle is calculated independently from the same source snapshot and request; it is not populated from returned output geometry.

## Isolated probe evidence

The native mirror probe produced one reflected Line with a null `Commit()` return and successful Undo. The earlier Python linear Journal setup produced no added curves. A later C# parameter probe's first two completed SketchPatternBuilder variants each showed zero global curves, zero target-Sketch curves, zero committed objects, and successful Undo; a later variant stalled during whole-Sketch constraint readback before completion. This is not evidence that all builder configurations fail, and it does not establish a kernel defect. The production array engine uses direct native curve transforms and passed the 17/17 native acceptance; the installed MCP also passed its separate 7/7 acceptance. The builder probe did not establish a kernel root cause.

The installed UF header documents `UF_TRNS_transform_objects` status 12 as “Cannot transform sketches or sketch curves.” That API is not used for the production arrays; transformed outputs are created from source geometry definitions and added directly to the target Sketch.

## Supported input geometry and limits

The production scope is native `Line`, partial circular `Arc`, and full-circle `Arc` geometry. Arc validation uses the native length together with its parameter interval and endpoint/midpoint geometry; it rejects nonfinite, degenerate, or multi-turn intervals. Ellipses and splines are unsupported and are rejected during read-only preparation.

Each request names the target `sketch_id` and every `source_curve_id`; mirror requests also name an `axis_curve_id` that must resolve to a different Line in that same Sketch. Counts include the source position. Circular pitch uses degrees with `0 < abs(angle_step_deg) < 360`; negative steps are supported, and pitches of 360 degrees or more are rejected during read-only request validation. The preview reports all positions and transforms, while output records contain only newly created curves. The request is bounded to 256 positions and 4096 source-position pairs. Duplicate or overlapping positions that cannot produce one distinct output per source/position are rejected during preparation.

The native mirror builder's NXOpen API documentation lists solid modeling, drafting, or geometric tolerance as required modules. This is a builder-specific API requirement, not a claim about general license availability or compliance.
