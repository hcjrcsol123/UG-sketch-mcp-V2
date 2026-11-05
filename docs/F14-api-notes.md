
# F14 NX API notes

These notes distinguish offline API observations, exploratory NX 2506 probes,
and product-adapter evidence. The acceptance and installation state is recorded
separately in the current handoff and F14 review.

`NXOpen.Features.HolePackageBuilder` exposes `Type` (`GeneralHole`,
`ThreadedHole`), `GeneralHoleForm` (`Simple`, `Counterbored`, `Countersink`),
`HoleDepthLimitOption` (`Value`, `ThroughBody`), `DepthOption`
(`ToCylinderBottom`, `ToConeTip`), `HolePosition` (a `Section`), and a Boolean
operation. Its ordinary-hole dimensions are expression properties, including
the individual diameter and depth properties and the countersink angle.
`ProjectionDirection` exposes `ProjectDirectionMethod` (`Vector` among the
options) and `ProjectVector` (`Direction`). `Section` supports point-only
selection and `AddSmartPoint`; the rule factory also exposes
`CreateRuleCurveDumbFromPoints`. `HolePackage` exposes `GetOrigins` and
`GetDirections`. The probes below establish the recorded direction and helper
relationships for the exercised cases.

For standard threaded holes the builder exposes `ThreadStandard`, `ThreadSize`,
`ThreadRotation`, `ThreadLengthOption`, `ThreadDepth`, `ThreadedHoleDepth`,
`TapDrillDiameter`, `RadialEngageOption`, and `PitchMultiplier`. There is no
obvious standalone pitch property on the builder. The local
`UGII/modeling_standards/NX_Thread_Standard.xml` contains `ThreadedHole`
records with standard, size, pitch and radial engagement; some standard/size
pairs have multiple engagement rows with different tap drill diameters. The
installed `UGII/ugii_env_ug.dat` identifies `UGII_THREADED_HOLE_STANDARD_DIR`
as the directory for `NX_Thread_Standard.xml`, with a default under
`UGII_BASE_DIR`. The effective NX process configuration must be used because
that directory can be overridden. The implementation obtains the effective
directory from the NX session and requires a unique standard/size/radial
engagement row. It independently reads the internal symbolic Thread's native
`ThreadBuilder.Pitch`, `PitchExp`, hand, depth and other parameters. The table
is configuration evidence, not the native readback. A symbolic thread is not
modeled helical thread geometry.

The contract uses an explicit target solid body, world point and
inward world direction, `simple`, `counterbore`, `countersink` or `threaded`,
and `blind` or `through`. Blind depth denotes the cylindrical
bottom; countersink angle is the included angle in degrees. Threaded requests
include explicit standard, size, radial engagement, pitch in mm, hand and thread depth.

subsequently exercised six ordinary-hole combinations and two blind thread
hands, including save and reopen. A separate readback probe

found the effective NX standard directory and the internal hole/Thread
relationships. This is API exploration, not product acceptance. Probe getters
can mark a part modified, and through-hole inactive depth values are not proof
of through depth.

The product's recorded entry scope is deliberately limited to
one planar face interior, with the axis in the inward face-normal direction,
in an arbitrary world orientation. A 0.01 mm inward/outward containment
witness can reject thin or locally complex material. Coverage checks require
single analytic faces, complete circular boundaries, adjacency and area within
their recorded tolerances; split walls, intersections and unsupported topology
are rejected. A radius below 0.05 mm or an axial wall segment below 0.1 mm is
outside this check's supported scale. These are software verification limits,
not manufacturing tolerances or a claim of full manufactured thread form.
For a through hole, the implementation also requires the maximum
axis projection of the entire target body to lie within 0.002 mm of the proven
exit plane; material elsewhere beyond that plane is rejected even when it would
not intersect the hole.

The Section's single CurveDumb rule collects the source point and produces a
distinct output point. The exercised NX build reports both at the requested
coordinates; the source Tag is retained for the live operation. The output
point is not a smart object, so a smart-parent relationship is not claimed.
For a symbolic thread, the two native Thread-owned Arc entities are checked
against the exact new curve inventory; existing curve geometry must remain
unchanged and the symbols must not enter a Sketch. Their query identifiers can
change after later edits, so stable geometric records are compared separately
at save/reopen.

Product-level native evidence is staged rather than represented as one full

contains seven selected ordinary and metric-thread passes plus the retained

records nine selected passes for the subsequent candidate, including metric
and inch threads, genuine Undo, cavity rejection, save/reopen, and old-hole
tamper controls. The reports label these `PASS_PARTIAL`; they are not a claim
that any one report alone is a complete acceptance run. The inch failure was
caused by an unqualified expression value and is retained; the subsequent
candidate uses NX `SetNumberValueWithUnits` with the explicit millimetre unit.

Execution uses the bridge ownership, idempotent operation records,
prepare/execute/get/fresh verify and UNKNOWN halt mechanisms. A single NX Undo
mark covers creation and readback failures. Preservation checks restrict allowed
new helpers, expressions and dependencies to proven feature-owned objects, and
check existing model data and non-target bodies. Native getter side effects on
the part's dirty state remain visible rather than being silently cleared.
