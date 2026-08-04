# CLAUDE.md — Dynamic Wind

Guidance for Claude Code when working in this repo. Vintage Story **1.22.3** code mod.

## What this mod does

Vanilla wind is a scalar that always blows due **East (+X)**. This mod gives wind a real,
slowly-evolving **direction** driven by a west-to-east jetstream plus moving low/high pressure
systems, and couples the rest of the world to it:

- **Particles, foliage sway, blowing snow, dropped items (incl. maple seeds), ground-movement wind** —
  follow the true wind vector *for free*, because vanilla routes them through `GetWindSpeedAt` and we
  rewrite that vector's X/Z in an `OnGetWindSpeed` hook. No patches for these.
- **Wind speed is additive, not multiplicative.** The hook keeps vanilla's own wind speed (SimplexNoise
  over time — its natural variation, *not* diurnal) as the baseline jetstream speed, points it along our
  direction, then ADDS front wind = `FrontWindStrength · max(0, |cell-only WindAt| − BaseStrength)` (the
  amount pressure CELLS — fronts/highs/lows — push the field above its calm baseline). This excess is
  deliberately measured with the terrain-thermal sea-breeze gradient (see "Terrain thermal temperature /
  sea breeze" below) EXCLUDED (`WindField.WindAt(..., includeTerrainGradient: false)`) — that gradient is real and
  still steers the wind's DIRECTION everywhere via the normal (terrain-included) `WindAt` call, but it's
  present near almost any coastline/terrain contrast regardless of whether a pressure system is nearby,
  so folding it into `frontExcess` would inflate average speed well outside any actual front. So a calm
  vanilla pattern can't zero the wind globally, fronts still blow when vanilla is calm, and an area with
  no active pressure cell falls back to vanilla's (possibly near-zero) speed — local calm is fine, global
  night-death is not.
- **Clouds** — Harmony postfix on both `CloudRenderer.CloudTick` and `FluffyClouds.CloudRendererMap.CloudTick`
  (whichever is active for the player's cloud-quality setting) rotates the cloud-drift vector to the wind bearing.
- **Windmills** — Harmony postfix on `BEBehaviorWindmillRotor.CheckWindSpeed`: power × cos(angle between
  wind and rotor axle). Head-on = full, from-behind = negative → spins backwards. `WindmillMinEfficiency`
  is the efficiency at exactly PERPENDICULAR, applied by rescaling the alignment curve onto
  `[MinEfficiency, 1]` — **not** by clamping the result (`max(|dot|, min)`, the original implementation,
  flattened every angle below the threshold to one identical speed and held that power right up to the
  crossing). Both the wind's speed and its alignment are eased per-rotor toward each tick's raw reading
  (`WindmillDirectionality.SpeedEaseRatePerSecond`) rather than snapped — real sails have rotational
  inertia — and reversal is decided from the EASED alignment too. Because perpendicular power is
  `MinEfficiency` rather than zero, easing the alignment alone never carries the mill through zero, so a
  reversal is DEFERRED behind an explicit output ramp (`ReversalRampRatePerSecond`): the rotor's own output
  drives down through zero, only THEN is `MechanicalNetwork.DirectionHasReversed` published, then it
  accelerates back up to target the other way — instead of a geared assembly snapping from full speed to
  full reverse in one tick. Exactly edge-on, the alignment sign is numerical noise and a square-on rotor
  would chatter every tick, so inside `PerpendicularDeadBand` the direction comes from which SIDE the wind
  passes on (the cross product) rather than from the alignment sign.
- **Sailboats** — the sail is **trimmable**, and thrust comes from how the wind meets the SAIL rather than
  merely the hull. `EntityBehaviorSailTrim` (`src/Entities/`, both sides, attached to the vanilla boat by
  `patches/boat-sailed-sailtrim.json`) swings the rig through ±`SailboatMaxTrimAngleDeg` (90° = fully
  feathered). **The whole swing — mast, yard AND both clew-rope sheets — is a real, BAKED shape
  `Animation`** (code `sailtrim`, added by `patches/boat-sailed-shape.json`), not a live per-frame pose
  write. Two things drove that: (1) an element that only INHERITS its mesh's joint id from an ancestor
  (rather than owning one itself) is skinned from THAT ancestor's captured transform, so writing
  `ElementPose.degOff*` directly on it renders as nothing — `MastORIGIN` and the clew-rope roots all do
  this by default, and an early attempt at swinging `MastORIGIN` via a live `degOffY` write (with
  `requireJointsForElements` covering it) never rendered any movement; (2) the clew-rope aim-and-stretch
  solve (see "Clew ropes" below) turned out to have its own live-math bug — reused scratch `Mat4f`
  matrices across `GetLocalTransformMatrix` calls without resetting them to identity in between (that
  method does NOT reset its own `output` — it post-multiplies onto whatever is already there), silently
  compounding stale data from the previous loop iteration into a wrong, non-obviously-wrong result. Real
  shape `Animation` keyframes sidestep both: elements referenced by a keyframe get their own joint through
  the engine's own (well-tested) animation-joint path regardless of `requireJointsForElements`, and the
  geometry itself is baked OFFLINE by `tools/keyframegen` (a standalone console tool referencing the real
  `VintagestoryAPI.dll` primitives — `Mat4f`/`Quaternionf`/`ShapeElement`, so it can't silently disagree
  with how the game itself poses the model) rather than solved live every frame, so there's no scratch-
  matrix reuse bug to reintroduce. `sailtrim` is keyframed every 1° across the full ±90° range (frame =
  angle + 90, 181 frames), covering `MastORIGIN`/`Mast7`'s rotation and — every frame, since the solve is
  nonlinear, not just the two endpoints — both sheets' rotation + Y-stretch, for all three sail positions
  at once (elements absent from whichever branch is currently tessellated are simply never resolved
  against, same as any other unreferenced element in a keyframe). `EntityBehaviorSailTrim.Pose()` just
  starts the animation and scrubs `RunningAnimation.CurrentFrame = 90 - CurrentAngleDeg` — MIRRORED, not
  `+ CurrentAngleDeg`: the keyframes were baked with `MastORIGIN`'s own `rotationY` set directly to
  `frame - 90`, which turned out to swing the visual sail normal opposite to what `SailboatWind`'s cos/sin
  convention assumes for the same signed `CurrentAngleDeg` (facing the wind visually read as feathered to
  the thrust model and vice versa). Requesting the mirror frame flips mast, yard AND both clew sheets
  together and self-consistently — each sheet was baked per-frame against whatever the mast's rotation
  WAS at that frame, not against an assumed port/starboard meaning, so mirroring the frame lookup alone
  fixes it without touching the baked keyframe data itself. This exactly mirrors the technique vanilla
  itself uses to drive the weathervane flag's frame from live wind (see below) — no per-frame matrix math
  left in the mod at all for this. `MastORIGIN` and `Mast7` both swing within THIS
  one animation (opposite sign — see "Mast7 counter-swing" below); the mast/yard/sail assembly needs no
  `requireJointsForElements` entry any more either, since being animation-keyframed already earns it a
  joint, though the clew-rope elements still need theirs listed there (nothing in their own keyframes
  triggers it, since they're the animation'S target, not its trigger — `requireJointsForElements` and
  keyframe-reference are two independent paths to the same joint assignment, and only one is needed per
  element). `MastORIGIN`'s authored `rotationOrigin` sits **10.5px forward of the mast column's true
  axis**, so the shape patch moves it onto the centre-line (`[24,2,1]` → `[34.5,2,1]`) — otherwise the mast
  would swing through an arc rather than spin in place; it carries no rotation of its own, so at rest that
  pivot move is a no-op. The target angle lives in `WatchedAttributes` (`dynamicwind:sailAngle`) so it is
  server-authoritative, synced and persisted for free; both sides then ease their own copy toward it at
  `SailboatSwingRateDegPerSec`, which keeps them converged without streaming the angle, and BOTH sides now
  drive the identical animation-scrub in `OnGameTick` — the earlier client/server split (client posing from
  `IAnimationManager.HeadController`, to win a per-frame ordering race against the animator's own blend)
  is gone, because scrubbing `CurrentFrame` has no such race: unlike a live pose write, it doesn't matter
  whether it happens before or after that frame's blend, only before the NEXT one. `Pose()` re-checks
  `IAnimationManager.IsAnimationActive` every tick before scrubbing, NOT `GetAnimationState(...) == null`
  — `GetAnimationState` looks up the animator's static table of every animation the SHAPE defines, so it
  returns non-null forever once the animator exists, regardless of whether that particular animation is
  currently active. Gating `StartAnimation` on that nullity (an earlier version of this did) means it only
  ever fires once, on the very first tick, and silently never again if the animation is ever deactivated
  (e.g. a re-tessellation rebuilds the animator with fresh, inactive `RunningAnimation` objects) — which
  read as "the animation just doesn't apply," no crash, no log. `IsAnimationActive` checks the real
  active-state flag instead; this is the exact pattern vanilla's own `EntityBoat.OnRenderFrame` uses to
  keep the weathervane animation continuously running. Two controls:
  hold-to-swing keybinds (`[` / `]`, `src/Client/SailTrimInput.cs` — **not** `,`/`.`: period opens chat to
  type a client command, so a period binding never reaches the hotkey) and right-clicking either sheet
  handle (`SheetLAP`/`SheetRAP`) for a `SailboatTrimStepDeg` nudge. Those two boxes come from invisible
  `SheetHandleL`/`SheetHandleR` elements the shape patch adds under `ORIGIN` — 7px cubes covering the deck
  cleat (`RibNc5` port / `knob6` starboard) AND every branch's knot. They hang on the HULL, not the rope:
  the knots are pinned to fixed anchors so a hull box covers them at any trim angle, it can't swing away
  from the cursor, and there are just two APs total instead of one per branch. Sizing matters because
  `EntityBehaviorSelectionBoxes` takes the box from the AP's PARENT element, and the knots alone are
  ~4×2.5×3px — under a third of a block, and far too easy to slip off while the boat rocks.
  `/dynamicwind sail` reports the whole chain (behaviour attached? swing animation active? angle moving?)
  for the boat you are sitting in, so an inert control localises to a link instead of needing a guess. The
  keybinds send **intent** (−1/0/+1), not increments — the server integrates it, so a lost packet can't
  accumulate drift and the sail stops by itself on dismount/disconnect. The Harmony postfix on
  `EntityBoat.SeatsToMotion` then computes `lift · drive`: how squarely the wind hits the sail's front
  face, times how much of that push points where the boat is going, scaled by wind magnitude via
  `SailboatWindSpeedInfluence` and floored at `SailboatMinThrust` so a badly trimmed boat still creeps
  rather than dying in the water. Vanilla zeroes `paddleForce` whenever sails are up, so with
  `sailPosition > 0` the whole of `__result.X` IS the sail term and scaling it is a complete substitution.
  `SailboatSailAngleEnabled = false` restores the old heading-vs-wind curve (tailwind +50% / headwind
  −10%) for existing worlds. Trim works in all three sail positions — see the clew-rope rig below. Tacking
  remains out of scope.
- **Mast7 counter-swing** — `MastORIGIN`'s subtree also holds the halyard rope (`RopeKnot`/
  `HalyardForeMast*`) and the weathervane flag (`FlagWrap`/`Flag`, see below), both of which run to fixed
  points and must NOT swing with the sail — but they hang off `Mast7`, a SIBLING of the sail/spar/pulley
  branches rather than a descendant of them, so there is no single node under `MastORIGIN` that captures
  "everything that should swing" without also capturing `Mast7`. The `sailtrim` animation keyframes
  `Mast7`'s own `rotationY` equal-and-opposite to `MastORIGIN`'s (−90°↔+90° where the mast is +90°↔−90°),
  cancelling the inherited swing for its subtree while leaving Mast1-6, the spar and the sails swinging
  normally. The tiny resulting position drift (`Mast7`'s pivot sits ~0.5px off the mast's true rotation
  axis) is sub-pixel and not worth solving for.
- **Weathervane flag** (`FlagWrap`/`Flag`, atop `Mast7`) — vanilla's `EntityBoat.OnRenderFrame` already
  scrubs this animation's frame to `atan2(CurrentWindSpeedClient.X, CurrentWindSpeedClient.Z) − boat.Yaw`
  every client frame, i.e. it's wired to follow real wind for free — but only if the entity attribute
  `weatherVaneAnimCode` is set; the base `boat-sailed.json` instead ships `disabledweatherVaneAnimCode`
  (a deliberately different key vanilla's own code never reads), which is vanilla's way of shipping this
  feature switched off. `boat-sailed-sailtrim.json` adds the real `weatherVaneAnimCode: "weathervane"`
  attribute to turn it back on. Because `CurrentWindSpeedClient` is exactly the vector this mod's client
  `OnGetWindSpeed` postfix rewrites, the flag then tracks true wind direction with no code of our own —
  same "vanilla already routes through the vector we control" pattern as particles/foliage/snow. Purely
  client-side and cosmetic (driven from `OnRenderFrame`, gated on `capi`); no interaction with sail trim
  or `Mast7`'s counter-rotation above beyond both targeting elements in the same branch.
- **REMOVED: sail billow.** A first attempt drove the cloth's own curvature (a second baked animation,
  rotating specific `SailUnfurled`/`SailHalf` panel joints) off `SailboatWind`'s `lift` value. Pulled back
  out — badly broken in practice (several panels went missing/misshapen) and needs a real redesign, likely
  with the affected sail sections remodelled rather than just rotated, before trying again. Don't
  reintroduce the old panel-rotation approach without addressing why those panels broke.
- **Clew ropes** (baked into the `sailtrim` animation — see "Sailboats" above; the live-solving code that
  used to compute this every frame, `src/Entities/ClewRopeRig.cs`, is REMOVED) — each sheet runs from a
  lower corner of the sail down to a fixed knot on the deck. In the shape the chain ROOT is at the clew,
  parented under the sail, so it swings for free and the FREE END is what must stay pinned — the tractable
  direction: aim the root at the fixed anchor and stretch it to reach, no IK. One rotation + one `stretchY`
  keyframed on the root drives every segment, because bones below it are plain `[0,16,0]` steps with no
  rotation of their own and both rotation and scale propagate straight down the pose tree to whatever hangs
  off the root — which is exactly the problem for the two leaves at the end of that chain, so BOTH now get
  their own keyframe track alongside the root:
  - **Pulley block** — gets the root's `stretchY` **reciprocally** (`1/scale`, its own `stretchY` keyframe)
    so it rides the rope without stretching along with it.
  - **Knot** — a knot tied to a fixed deck post shouldn't visibly SPIN just because the rope above it
    swings, but with no rotation of its own it inherits the root's full solved rotation verbatim (every
    bone in between is a pure translation). Its own keyframe rotation is the counter-rotation that undoes
    that and restores its REST orientation instead — `mC⁻¹ · RestRotation` composed with its own authored
    baseline, where `mC` is the root's per-frame solved rotation matrix (computed in `Solve()` right before
    the Euler extraction that becomes the root's own keyframe). Both leaves are keyframed at EVERY frame
    (not just the two endpoints), same as the root, since neither the scale nor the rotation is linear in
    trim angle.

  The root's own solve is EXACT, not axial: aiming the root's local +Y at the anchor leaves each knot up
  to a third of a block adrift at full trim (they hang a few px off the centreline), so the aim targets the
  knot's own offset `u` — scale follows from `|S·u| = |d|` alone, then the rotation is the rest orientation
  composed with the minimal rotation carrying the scaled offset onto the target. Rest pose (frame 90, angle
  0) reproduces vanilla exactly for all three keyframed elements (rotation ~1e-6°, scale ~1.0000000) so
  nothing shifts on load. `tools/keyframegen/Program.cs` computes this per branch:
  anchors are captured ONCE per branch at rest, lifted into **`MastORIGIN`'s parent space** — the nearest
  frame that does not move when the rig swings, so a deck knot is fixed there by definition — by walking
  the parent links from the clew root back up to the rotator, so the shape's nesting can change without the
  tool knowing the intermediate bone names. The three branches' deck ends differ by ~1px, so sharing one
  anchor would jump the knot when furling — hence a separate capture (and separate keyframe track) per
  branch. Frame maths goes through the engine's own primitives (`ShapeElement.GetLocalTransformMatrix`,
  `Mat4f`, `Quaternionf.RotationTo`) rather than a private reimplementation, so it cannot silently disagree
  with how the renderer poses the model — hence everything is in BLOCKS, not shape pixels. The one
  exception is the Rx·Ry·Rz euler extraction: the engine ships `Quaternionf.ToEulerAngles`, but that is a
  **ZYX** yaw-pitch-roll triple while `ShapeElement` composes **Rx·Ry·Rz**, so feeding one into the other
  would quietly skew the rope. **`GetLocalTransformMatrix` does NOT reset its own `output` matrix to
  identity** — it post-multiplies Translate/Rotate/Scale directly onto whatever `output` already holds, so
  a scratch matrix reused across a loop's iterations (as the original live `ClewRopeRig` did, and as the
  first draft of `keyframegen` also did before this was caught) silently compounds stale data from the
  previous element instead of producing that element's own transform — the result isn't visibly
  degenerate, just quietly WRONG (e.g. reasonable-looking but nonzero rotation/scale at rest, where the
  correct answer is exactly zero/one). Every `GetLocalTransformMatrix` call site in `keyframegen` calls
  `Mat4f.Identity()` on its scratch matrix immediately first because of this.
- **Weather** — `WeatherCoupling` (Harmony postfix on `WeatherSystemBase.GetRainCloudness`) raises
  cloudness/rain inside lows and lowers it in highs — storms form in **low** pressure, highs are
  fair-weather subsidence, matching real synoptic charts (see [noaa.gov/jetstream/wxmaps](https://www.noaa.gov/jetstream/wxmaps)).
  Lows also add circulation, so storms = stronger, swirlier wind. The coupling is **MULTIPLICATIVE**, not
  additive: `__result *= exp(StormCouplingStrength · clamp(P) · typeMult · PrecipLogGain)`, so a low scales
  vanilla's existing cloudness field UP and a high scales it DOWN, with low/high exact reciprocals. Vanilla's
  own field already carries organic broad-scale texture, so modulating it yields patchy bands inside a storm
  and patchy clearing inside a high **for free** — which is why there is no longer any per-cell noise map
  (`WindField.CellWeatherNoise` is REMOVED; so is the older `WindField.StormBiasAt` before it). It's also
  sign-safe by construction: a high can only ever scale cloudness down, whereas subtracting a zero-mean
  noise could *raise* it over half a high's footprint (a real bug that existed). The LOW side of the
  exponent is weighted by `WindField.FrontRegimeBiasAt`'s front-type multiplier (occluded > cold > warm >
  stationary), itself `StormLikelihood`-weighted so an immature low hasn't earned full intensity.
  **Deliberate consequence:** the coupling is proportional, so a region vanilla left completely dry stays
  dry — a pressure system modulates an existing moisture pattern rather than inventing one. There is
  deliberately **no upper clamp** on the result: vanilla clamps where it needs to (`Event_OnGetClimate` does
  `Clamp(cloudness, 0, 1)` into `RainCloudOverlay` and `Clamp(cloudness − 0.5, 0, 1)` into `Rainfall`), but
  `GetPrecipitation` is `Max(0, cloudness − 0.5)` with NO ceiling — so capping cloudness at 1 here silently
  caps precipitation at 0.5 and flattens every storm core and the radar's red band. Headroom above 1 is what
  makes an intense low read as intense. (Lightning rate is `distantLightningRate × RainCloudOverlay`, i.e.
  linear in cloudness, so this also governs how often distant thunderstorms fire.)
- **Cloud/precip drift** (`WeatherCoupling.DriftPrefix` + `WindField.CloudDriftAt`) — vanilla's precipitation
  noise barely moves and never follows the wind: `getPrecipNoise` samples at `posX/18 + totalDays·18` with
  **no time term on Z at all**, so the pattern slides due WEST at a hardcoded **324 blocks/day** (13.5/hour),
  can never travel north/south, and ignores wind entirely — ~30× slower than cells advect (`AdvectionSpeed`
  400 blocks/**hour**). Its third noise axis (`totalDays·4` at octave frequency 1/150) also only decorrelates
  over **~37 game-days**, which is why the same pattern can sit overhead for months. This became conspicuous
  once precipitation went multiplicative, since vanilla's noise is now the visible texture rather than one
  contribution among several. A Harmony **prefix** therefore shifts `posX`/`posZ` by the accumulated wind
  drift before vanilla samples (sampling UPWIND makes the pattern travel DOWNWIND) and subtracts vanilla's
  own westward term so wind is the only thing moving it. `conds`/`wgenRain` are untouched, so a region's
  underlying climate stays put and only the weather riding over it moves. The drift is a TIME INTEGRAL of the
  base flow, so it is server-accumulated (`WindFieldSimulation.AccumulateCloudDrift`, sampled from the global
  jetstream bearing at a reference position — a position-DEPENDENT offset would shear the noise field rather
  than translate it) and synced in `WindFieldState` (`DriftX/Z` + `DriftVelX/Z`, re-based to the cell anchor
  on the wire); the client extrapolates linearly exactly as it does cell positions, so radar and real rain
  can't disagree. It carries its OWN timestamp (`WindField.CloudDriftHours`) rather than riding on
  `AnchorHours`, since the cell anchor moves on every spawn/expire; it is persisted through `Save`/`Load`
  and re-anchored alongside the cells on reload/empty-server resume, or the texture would snap back to its
  origin (or jump thousands of blocks) on the first tick after a gap. The postfix reads the TRUE position via
  Harmony `__state`, since pressure cells live in world space, not the noise field's drifting frame.
- **Fronts** (`FrontComputer`/`FrontMapLayer`) — `FrontComputer` traces pure contour geometry only
  (points + arc-length + a facing reference per `FrontLine`); `FrontMapLayer` renders it procedurally as
  vector shapes, not icon sprites — a continuous stroke built from thin quads bridging every fine-sampled
  segment (so the line always follows the curve exactly, however sharply it bends, instead of
  angle-lerped fixed-size icon tiles gapping/overlapping there), plus periodic decorations (cobalt-blue
  triangles for cold, crimson hemicircles for warm — the SAME cobalt/crimson `FrontColors.ColdBlue`/
  `WarmCrimson` used to tint high/low-pressure wind arrows — alternating for stationary, all in a deep
  alizarin purple — `FrontColors.OccludedPurple`, blended from the two — for occluded variants) built from
  procedural `MeshData` (`Utils/ShapeMeshUtil.cs`) via `Utils/FrontColors.cs`. Only LOW cells carry fronts; a high is fair-weather
  subsidence with no frontal boundary of its own (still gets an H marker, no line). A moving, open-wave low
  renders the classic Norwegian-cyclone-model pair — BOTH a warm front leading ahead of the low's own
  direction of travel (`atan2(VelZ, VelX)`) and a cold front trailing behind it, meeting at the low's own
  centre — rather than a fixed compass/jetstream direction or (an earlier design) only one or the other.
  Each is tilted off the pure travel/anti-travel axis toward its real-world side by `WindField.WaveFrontTiltAngle`
  (warm leads AND tilts poleward — where warm air overruns; cold trails AND tilts equatorward — where cold
  air digs in), via `WindField.PolewardZSign`/real latitude — giving the classic asymmetric "comma" shape
  instead of two lines sitting exactly opposite each other through the low's centre. Neither line ever
  splits through the low's middle or starts at a single point: each IS that side's own elliptical EDGE
  (`EllipseGeometry.RadiusToward`, accounting for `RadiusMinor`/`Orientation`) traced across a wide arc
  (`FrontComputer.BuildLeadingEdgeArc`, ±`ArcHalfAngle` around its own bias angle — a cap hugging the whole
  front-facing side of the low), with that boundary radius scaled outward by ITS OWN reach
  (`WarmFrontReach`/`ColdFrontReach` respectively) when the cell is ISOLATED. When it meaningfully overlaps
  another significant cell (`FrontComputer.HasSignificantOverlap`, a plain distance-vs-combined-radius
  check), the single-cell ellipse would no longer match the true (summed) field — that case instead traces
  the ACTUAL combined pressure field with marching squares over a small LOCAL grid around the overlapping
  pair (`FrontComputer.BuildFrontLineViaOverlap`, `Utils/MarchingSquares.cs`), at the level
  `|Strength| · exp(-reach²)` (equivalent to the analytic radius-scaling — the two agree exactly at
  reach = 1), restricted to the same bias-angle wedge and chained into one polyline
  (`MarchingSquares.ChainSegments`) since front lines need arc-length ordering for their decorations.
  Isolated systems (the common case, given the low `MaxActiveCells` cap and spawn logic that already keeps
  new cells apart) are unaffected by this path. Three earlier approaches were tried and discarded before
  landing here: an explicit Bezier JOIN connecting a front's curve to a neighbouring low's, which could
  self-intersect or collapse to near-zero length when several systems sat close together (rendering as
  tiny, wrong-colored blobs); a free march trailing outward from the arc's ends (gently deflected by nearby
  cells' background pressure gradient) plus a sine "wobble" for organic texture, which read as jagged/noisy
  once cells grew to synoptic scale; and even the plain analytic arc alone stopped being enough once two
  systems could sit close enough to genuinely interact, which is what the marching-squares fallback now
  handles. Stationary and Occluded fronts still render only a SINGLE line (a real stationary/occluded front
  is one boundary, not a warm/cold pair), picking their bias angle FROM a nearby cell's direction
  (bearing-to-neighbour / catch-direction respectively) but never drawing an explicit connecting curve to
  it — and go through the same isolated/overlap dispatch as the Warm/Cold case. `WindField.FrontRegimeBiasAt`
  weights precipitation-TYPE intensity (occluded/cold/warm/stationary) toward EXACTLY the same
  leading/trailing axes each low's front lines actually draw (`WindField.MovingFrontAngles`, also what
  `FrontComputer` calls to draw the two lines) — occluded: the single catch/equatorward axis; stationary:
  bearing to its nearest other significant cell; moving: both the leading/warm and trailing/cold axes —
  rather than an independently-chosen geography split that can silently drift out of sync with what's
  drawn (as the old, purely poleward/equatorward convention did once fronts switched to leading/trailing a
  low's own direction of travel). See "Weather" above for how that regime weighting actually feeds into
  precipitation (it scales the LOW side of the multiplicative cloudness exponent).
- **Pressure falloff shape** (`WindField.PressureFalloff`) — a cell's pressure decays as `exp(-√norm2)`,
  NOT the gaussian `exp(-norm2)` its circulation/steering terms use. Both agree exactly on the cell's own
  elliptical boundary (`norm2` = 1 → e⁻¹ ≈ 0.37); the difference is the tail. Front lines are drawn at
  `radius × reach` with reach growing to `MaxFrontReach` = 3, i.e. `norm2` = 9 — under a gaussian that's
  e⁻⁹ ≈ 0.0001, so the pressure differential along a mature front was four orders of magnitude down and
  everything derived from it (weather coupling, isobars, map tint, gradient wind) was effectively dead out
  there. `exp(-√norm2)` gives e⁻³ ≈ 0.05, ~500× stronger. NOTE the falloff was ALREADY elliptical
  (`EllipseGeometry.Norm2` divides along-stream by `RadiusMinor`, cross-stream by `Radius`) — the dead far
  edge was never a circular-vs-elliptical problem, it was the gaussian's tail. `FrontComputer`'s
  overlap-trace level MUST go through `PressureFalloff` (level = `|Strength| · PressureFalloff(reach²)`)
  rather than hardcoding a formula, or the traced contour lands at a different radius than the analytic
  arc it falls back to.
- **Isobars** — rendered in the SAME layer as fronts (`FrontMapLayer`, not a separate map layer), but
  computed separately from markers/fronts (`FrontMapLayer.ComputeIsobars`, not `FrontComputer.Compute`)
  since — unlike the front lines' per-cell geometry — isobars are a whole-viewport visualization with no
  single-cell analytic shortcut available in the first place: they're traced unconditionally via marching
  squares (`Utils/MarchingSquares.cs`) over the ACTUAL combined pressure field
  (`WindField.CellPressureAt`, cells only — which is now the whole horizontal pressure field anyway,
  minus the altitude lapse), sampled across the whole visible viewport on a grid coarsened exactly like
  `DopplerRadarMapLayer`'s own grid (fixed base step, doubled whenever the visible area would exceed a
  cell budget) so resolution scales down automatically when zoomed out. Unlike fronts (lows only), isobars
  are drawn around EVERY significant cell, highs included — a real chart draws them around anticyclones
  too — since both signs are traced at each level. Levels are spaced at a FIXED absolute pressure step
  (`FrontMapLayer.IsobarStep`), real-chart style: a weak system shows one or two widely-spaced rings, a
  strong one shows many tightly-packed ones — that density variation is a feature, not something to
  normalize away. Raw marching-squares segments are drawn directly (no chaining into ordered polylines,
  unlike overlapping front lines) since isobars have no decorations/symbols placed along arc length.
  `FrontMapLayer` draws them first/furthest back (faint, alpha fading per ring outward from the innermost)
  so they read as background context behind the front lines/markers.
- **Front reach** — each low's cold and warm sides carry an independent radial reach multiplier
  (`PressureCell.ColdFrontReach`/`WarmFrontReach`, start at 1) grown every hour in `WindFieldSimulation.ApplyOcclusion`
  — cold faster (`ColdReachGrowthPerHour`) than warm (`WarmReachGrowthPerHour`) at the base rate, since real
  cold fronts advect/extend faster — so the two sides visibly move/expand at different rates instead of one
  symmetric contour. Each side's growth rate is further scaled by `DeltaTSlowdownScale` against the
  `AirMassTemp` of whichever OTHER pressure cell currently sits on that side (`FindNearestCellOnSide`): a
  similar air mass offers little resistance and the front advances near its base rate, a starkly different
  one slows it — so cooler Δtemp (more similar air masses) → faster front movement, higher Δtemp → slower.
  With no cell on a side, growth uses the unmodified base rate. `FrontComputer.BuildLeadingEdgeArc` scales
  each front's own traced arc boundary radius by ITS side's reach before rendering (see "Fronts" above) —
  both fronts are always drawn for a moving wave low, so both reaches are directly visible at once, growing
  independently. `ColdFrontReach` also still drives the front occlusion catch check below.
- **Front occlusion** — a real two-cell event: a low's advancing cold front physically CATCHES another
  pressure cell (low or high, whichever `FindNearestCellOnSide` finds equatorward of it), not a fixed ratio
  against its own warm front or an age timer. `WindFieldSimulation.ApplyOcclusion` triggers a catch once
  `ColdFrontReach` clears `OcclusionCatchMinReach` (1.15×, guards against an instant catch — cells are
  deliberately sized to overlap into broad blobs, so a fresh cell's own radius can already overlap a
  neighbour at reach 1) AND the distance to that equatorward cell falls under
  `OcclusionCatchRadiusFactor × ColdFrontReach × own Radius + OcclusionCatchTargetRadiusFactor × target Radius`.
  The relative `AirMassTemp` of the catching low vs. the cell it caught then classifies the occlusion
  (`PressureCell.OcclusionKind`, 1 = cold / 2 = warm) — catching air colder than or equal to the caught
  cell's behaves like a cold front (drawn with triangle pips), warmer behaves like a warm front (drawn with
  hemicircle bumps), matching real synoptic classification, both in `FrontColors.OccludedPurple` instead of
  their normal cobalt/crimson; `OcclusionKind` 0 (unset, e.g. a save from before this field existed) falls
  back to alternating triangle/hemicircle (like Stationary) still in purple. Once classified: storm
  likelihood drops hard, and its remaining lifetime is cut to `OcclusionDeathSpeedup` (40%) — occluded
  lows die out faster, like real decaying cyclones. `PressureCell.OccludedUntilTotalHours` doubles as a
  one-shot latch so a cell only occludes once. (Unrelated to the indoor/underground "Occlusion" wind
  shielding below, besides sharing the name.)
- **Indoor/underground occlusion** — a wholly separate mechanic from front occlusion above: config
  `OcclusionEnabled` gates `DynamicWindModCommon.ComputeWindSpeed` multiplying wind speed by
  `OcclusionFactor(pos)`, which reduces or zeroes wind for enclosed/underground positions. If
  `OcclusionRoomsZeroWind` and the position is inside a room per the room registry, wind is zeroed
  outright; otherwise the factor is derived from sun-exposure light level scaled against
  `OcclusionLightThreshold` and shaped by `OcclusionSharpness` (>1 keeps near-open spots closer to full
  wind), floored at `OcclusionMinFactor` so fully enclosed spaces never reach exactly zero via this path.
  Skipped for ambient/UI wind queries (`IsAmbientPlayerQuery`).
- **Air-mass temperature** — each `PressureCell` carries an intrinsic `AirMassTemp` in [−1, 1] (cold to
  warm) independent of whether it's a low or a high, set once at spawn from birth latitude (colder toward
  the map's N/S edges), birth-position season (`Calendar.GetSeasonRel`, already hemisphere-aware), and
  random jitter — so a cold low and a warm high can both exist, same as a polar low or a subtropical
  high in reality. `WindField.CellTemperatureAt` blends nearby cells' `AirMassTemp` (falloff × |strength|
  weighted) and drives the real temperature shift in `DynamicWindModCommon.OnGetClimate`
  (`FrontTemperatureMax`), replacing the old low-pressure-is-always-hot assumption. (Same-type cell
  merging was removed — it interfered with the occlusion catch-target search below, and active cell count
  is already bounded by spawn rate + despawn distance, so it wasn't needed to keep cell count in check.)
  `AirMassTemp` also biases a cell's own advection: `WindFieldSimulation.Advect` (via the shared
  `AirMassBiasedAngle` helper) rotates a cell's target advection bearing toward the pole (warm) or equator
  (cold) side by up to `AirMassTempMoveBiasMaxAngle` (~25°), scaled by `AirMassTemp` and
  `WindField.PolewardZSign` — mirroring how `SeasonalBiasZ` tilts the base jetstream angle rather than
  pushing a raw Z velocity component, so the bias stays meaningful relative to whatever the local flow is
  actually doing. Applied to every cell (low or high) whenever `AdvectWithWind` is on. `WindFieldSimulation.Spawn`
  pre-biases its upwind aim the same way (estimating the new cell's `AirMassTemp` at the anchor position)
  so a systematically warm/cold cell still tends to drift back through the player instead of consistently
  leaning past them.
- **REMOVED: terrain-thermal seeding and heat/cold waves.** Mobile cells seeded from terrain/water thermal
  hotspots (`ThermalSeeding` / `TrySeedOne`) and the rare heat-wave/cold-wave highs built on the same
  water-hotspot siting path (`HeatColdWaveEnabled` / `TrySeedHeatColdWave`, with `PressureCell.IsHeatColdWave`
  / `WaveTemperatureDelta` and `WindField.HeatColdWaveDeltaAt`) were all deleted — terrain-driven cell
  formation no longer made sense once cells became synoptic-scale, and the water-only siting gate it
  depended on locked out inland players entirely. **Every mobile cell now comes from the jetstream spawner
  (`WindFieldSimulation.Spawn`) alone**, and `TerrainThermal` is now a purely static TEMPERATURE field
  (see the next bullet) that spawns nothing. `TerrainThermal.IsWater` went with them (its only caller was
  seed siting). Don't reintroduce any of this without a new design — the reasons it was cut are above, not
  merely a tuning problem.
- **Terrain thermal temperature / sea breeze** (`TerrainThermal`, `WindField.TerrainTempFunc`) — the
  terrain signal is a near-surface **temperature anomaly in °C** (`TerrainThermal.TemperatureAnomalyAt`,
  + = warmer than ambient), NOT a pressure term: warm, sun-exposed dry land runs warmer than its
  surroundings, always. Water's sign is **seasonal**, matching real air/water thermal contrast: in the warm
  season water is COOLER than the land beside it (the marine-layer/sea-breeze pattern), in the cold season
  relatively warm water is WARMER than the frozen land beside it (real nor'easters, polar lows,
  autumn/winter lake-effect systems) — blended continuously by season (`TerrainThermal.SeasonWarmthAt`,
  same cosine pattern as `WindFieldSimulation`'s other seasonal-warmth lookups) so shoulder seasons taper
  toward neutral rather than flipping outright. Modulated by time of day + temperature; water's magnitude
  scales with **depth** (sea level − `GetTerrainMapheightAt` seabed, full at `WaterFullDepth`≈20 blocks) so
  shallow coasts read near-neutral, and is STRONGER at night (the contrast sharpens once land stops
  radiating) — which is what reverses the coastal flow into a night-time land breeze for free.
  Two consumers, both reading `WindField.TerrainTempFunc` (set per side: each samples its own world's
  surface height + `GetClimateAt`/season, cached — see `ThermalCache` below): the climate temperature shift
  in `DynamicWindModCommon.OnGetClimate` (`ThermalTemperatureMax`), and the **sea-breeze wind** term in
  `WindField.WindAt`, a thermally DIRECT circulation that blows toward warmer air (up the temperature
  gradient, into the thermal low), scaled by `SeaBreezeStrength` and capped at `SeaBreezeMaxWind`.
  **It deliberately no longer feeds `WindField.PressureAt` at all** (the old `ThermalPressureFunc` /
  `ThermalStaticScale` static pressure baseline is REMOVED): pinning a pressure system to the ground is
  unphysical, and driving the coastal flow off the temperature field directly is both the physically
  primary formulation and one fewer indirection. Horizontal pressure is therefore purely synoptic (moving
  cells), with the altitude lapse on top. Note the sea breeze is a DIRECTION contribution as far as speed
  accounting goes — `ComputeWindSpeed` measures front excess with `includeTerrainGradient: false` precisely
  so this ever-present coastal gradient can steer the wind everywhere without inflating average speed
  outside any real pressure system.
  NOTE: `WeatherCoupling.OnGetClimate` calls into the thermal lookup → `GetClimateAt`, so it uses a
  `[ThreadStatic]` reentrancy guard to avoid infinite recursion.
- **Real latitude** (`WindField.LatitudeFunc`) — Vintage Story's actual climate latitude is NOT a single
  equator at the map's centre with poles at its edges: it's a repeating triangle wave in world Z (period
  `4 × polarEquatorDistance`, a world-config value defaulting to 50000 blocks, phase-shifted by a per-seed
  offset), so poles and equators alternate repeatedly across the map. `LatitudeFunc` is a thin per-side
  wrapper around the game's own `Calendar.OnGetLatitude(z)` (set once the calendar's ready, re-checked per
  call rather than captured, since world-gen wires it up asynchronously) — never reimplemented, since the
  period/phase aren't derivable from `MapSizeZ` alone. `WindField.PolewardZSign(z)` differentiates it
  numerically (which local direction increases `|latitude|`) rather than assuming a fixed hemisphere, so
  "poleward" correctly flips every `2 × polarEquatorDistance` blocks instead of once at the map midline.
  `WindFieldSimulation.ComputeAirMassTemp`'s birth-latitude term (colder near a real pole) uses the same
  function. Falls back to the old map-midline heuristic (`MapCenterZ`) only if `LatitudeFunc` isn't wired
  up yet.
- **Seasonal jetstream bias** — the westerly base flow tilts N/S with the seasons: poleward in summer,
  equatorward in winter. So a northern-hemisphere section is southwesterly in summer / northwesterly in
  winter, and a southern section is the mirror. Implemented as a single signed factor `WindField.SeasonalBiasZ`
  ∈ [−1,1] added to the base jetstream angle (`SeasonalBiasMaxAngle`, config `SeasonalBiasMaxAngleDeg`,
  default 45°). The server recomputes it each tick from `Calendar.GetSeasonRel` + `GetHemisphere` at a
  reference position (`summerness = cos(2π·(seasonRel − 0.597))`, times the poleward Z sign: N pole = −Z,
  S pole = +Z) and syncs it in `WindFieldState`. It is **globally uniform** because VS offsets southern-
  hemisphere seasons by half a year (`GetSeasonRel` returns `(YearRel+0.5)%1` there), so the hemisphere flip
  and the season offset cancel — one reference sample is correct everywhere. Shows in `/dynamicwind debug`.
- **Pressure dynamics** (flat `*` config knobs, evolved in `WindFieldSimulation`) — cells are a living
  field, not static gaussians:
  - **Advection** — each server tick a cell EASES its velocity toward the *local background wind*
    (`WindAt(x,z,t,excludeCell)`, self excluded) rather than snapping straight to it — an exponential
    approach at `VelocityEaseRatePerHour`, real inertia — and its current velocity is stored so the client
    extrapolates linearly between snapshots. `AdvectionSpeed` (blocks/game-hour per unit wind). Off →
    falls back to the fixed jetstream velocity.
  - **Dispersal** — `DispersalRate` grows each cell's radius and decays its strength per hour; cells expire
    once strength falls below `MinStrength` (or hit their lifetime backstop). So systems spread out and fade.
  - **Bernoulli** — `BernoulliStrength`: faster-than-nominal flow lowers a cell's pressure (toward low, +),
    stagnant air raises it. Sampled once per tick from the same background wind (no recursion).
  - **Altitude lapse** — `WindField.PressureAt(x,y,z,t)` adds a vertical term: real air pressure falls with
    height, read as "more low" (+), reaching +`AltitudePressureDrop` (default 0.5 = −50%) at the build ceiling,
    0 at/below sea level. Vertical-only, so it never alters wind — only the displayed/weather-coupled pressure
    (map tint, `WeatherCoupling`, `/dynamicwind debug` all pass their Y). `SeaLevel`/`WorldHeight`/`MapCenterZ`
    (= `MapSizeZ / 2`, now only a fallback for `PolewardZSign` before `LatitudeFunc` is wired — see "Real
    latitude" above) are set per side from world geometry, not synced.
- **Directional steering** — a pressure cell's circulation keeps bending the ambient wind's DIRECTION well
  beyond the (narrower) range at which it still meaningfully pushes extra SPEED — real large-scale steering
  flow around a system reaches farther than its local wind-speed footprint. `WindField.WindAt` gates the
  circulation term's SPEED contribution with the usual falloff, but separately accumulates a
  magnitude-independent target bearing (each cell's own circulation direction, sign-only) under a WIDER
  falloff (`DirectionalFalloffFactor` < 1 widens it relative to the speed falloff), then blends the current
  wind bearing toward that weighted target — a pure rotation that preserves whatever magnitude the field
  already has, so it reorients the flow without adding speed on its own. Applied only to the
  jetstream+circulation portion, before the separate pressure-gradient bleed below.
- **Pressure-gradient wind** — wind bleeds down the pressure gradient (high → low), so the synoptic cells
  actually push the air. `WindField.WindAt` adds `PressureWindStrength · ∇P` (numeric gradient of
  `PressureAt`, clamped). Runs on both sides. The coastal sea breeze is NOT part of this term — it's a
  separate thermally direct term off the temperature gradient (see "Terrain thermal temperature / sea
  breeze" above).
- **Terrain (orographic)** — in `WindField.WindAt` on BOTH sides via a per-side `TerrainHeightFunc`
  (server: Algernons Terrain Sampler at any coord; client: rain-map height for loaded chunks, so the minimap
  shows it). Wind climbing into a slope slows (stagnation) and curves along the contour around the obstacle;
  wind sheltered by higher terrain upwind slows in the lee wake. Tunables: `OrographicSlowdownFactor`,
  `OrographicDeflectionStrength`, `OrographicWakeFactor`.
- The terrain-temperature and terrain-height lookups are wrapped in `ThermalCache` (coarse tile + time bucket,
  thread-safe) because `WindAt` samples them several times per call on a hot path; this also keeps the
  weather-coupling re-entry rare.
- **Wind overlay** (`WindArrowMapLayer`) — a fixed 24×24 grid of arrows on the **full world map only**
  (skips the minimap HUD via the `GuiElementMap.snapToPlayer` flag). Arrow length scales **logarithmically**
  with wind speed so low speeds stay legible.
  Each samples `GetWindSpeedAt` at its cell's world midpoint (player Y), rotates to the bearing, and scales
  its length by wind speed (~10% at 1% speed → 100% at full; hidden at 0%). The greyscale arrow art is tinted
  by pressure (from the synced field's `PressureAt`): low → crimson `#FF1000`, high → cobalt `#2651FF`,
  neutral → white. Fixed screen grid → zooming out makes each arrow cover more world distance. Registered via
  `WorldMapManager.RegisterMapLayer`; arrow art at `assets/dynamicwind/textures/icons/wind-direction.png`.
  Resampled ~7×/s and cached for perf.

## Axis convention

`+X = East`, `+Z = South` (so North = −Z, West = −X). Jetstream base flow = +X. Bearing in
`/dynamicwind debug` is `atan2(Z, X)` degrees: 0 = E, 90 = S, ±180 = W, −90 = N.

Because the map is north-up with +Z pointing *down* (south), the screen-CCW tangent is `(rz, −rx)`, so
lows circulate **counter-clockwise** and highs **clockwise** (NH convention). Pressure-cell radii are
sampled directly from `[CellRadiusMin, CellRadiusMax]` (user config, default ~15,000–30,000 blocks — large,
synoptic-scale systems) rather than the server's loaded-chunk extent — jet-spawned systems
(`WindFieldSimulation.Spawn`, now the only spawner) sample that full range. New cells spawn
~40,000 blocks (`SpawnDistanceBlocks` ± `SpawnDistanceJitter`) from a random online player, generally
upwind (scattered within `SpawnAngleSpreadRad` of the pure-upwind bearing) so they drift back through the
player's area — like a real system approaching from a distance. That target distance is capped by the
cell's own radius (`SpawnDespawnSafetyMargin` inside the `DespawnMargin` threshold) so a cell never spawns
close enough to immediately despawn, and if the scattered position still lands within
`SpawnPlayerAvoidRadius` of ANY online player (not just the anchor one), it's pushed further upwind
instead (`WindFieldSimulation.Spawn`/`NearestOnlinePlayerDistance`).

## Architecture

Three-class ModSystem split (modelled on `D:\satyrnidae\vs-balladeer-class`):

- `DynamicWindModCommon` (`ShouldLoad => false`) — loads/validates config, registers the single
  dedicated `"dynamicwind"` network channel + message types, owns the shared `WindField` and the
  `OnGetWindSpeed` hook (with a virtual orographic step).
- `DynamicWindModServer` (`ShouldLoad => Server`) — owns `WindFieldSimulation`, is the authoritative
  config + field source, syncs both (on `PlayerJoin` and on change), applies the server Harmony patches
  (windmill, sailboat) + `WeatherCoupling`, orographic via the sampler, and the `/dynamicwind` command.
  `WindFieldSimulation` itself runs on its own dedicated thread (`WindFieldSimulation.Start`/`Stop`,
  spun up via `TyronThreadPool.CreateDedicatedThread` once `SaveGameLoaded` fires), ticking independently
  of the main server tick — its per-cell math (advection, occlusion's O(n²) neighbour search,
  weather-feedback climate sampling) is this mod's dominant cost and has no reason to compete with the
  main tick for CPU time. The main-thread `RegisterGameTickListener` (still 2s) now only refreshes a
  lock-free player-position snapshot (`WindFieldSimulation.CapturePlayerSnapshot`, an immutable array
  swap) the sim thread reads for spawn/despawn distance checks — enumerating `AllOnlinePlayers`/
  `NearestPlayer` directly from the sim thread would race with players joining/leaving on the main
  thread — and broadcasts the synced state. All cell-list mutation is already behind `field.CellsLock`,
  so `Save`/`Load`/`Snapshot` (called from the main thread) stay safe against the sim thread.
- `DynamicWindModClient` (`ShouldLoad => Client`) — mirrors config + field, runs the hook, patches the
  cloud renderer, and wires the optional ConfigLib GUI.

Core idea: `WindField.WindAt(x, z, totalHours)` is a **deterministic** function of a small state
(base params + pressure-cell list). The server simulates and broadcasts only that state
(`WindFieldState`); the client re-evaluates the same function locally, so no per-block wind streaming.
Cell positions are advected from a single anchor time, so one snapshot stays correct between updates.
`DynamicWindRuntime` is a static bridge so Harmony patches can reach the per-side field/config.

## Conventions

- **Config** (`DynamicWindConfig`): server-authoritative, protobuf-synced. The *same* class is the JSON
  config (`LoadModConfig`/`StoreModConfig`) AND the network packet (`[ProtoContract(ImplicitFields=AllPublic)]`,
  public auto-properties). `[Description]/[Range]/[Category]` attributes drive the optional ConfigLib GUI.
  All settings are **flat top-level scalars** (e.g. `OrographicSlowdownFactor`, not `Orographic.SlowdownFactor`):
  ConfigLib's managed-config reflection only sees top-level scalar properties — it does NOT recurse into nested
  objects — so flattening makes every knob appear in the GUI. `[Category]` groups them visually. Each setting
  needs a `setting-<PropertyName>` lang key in `assets/dynamicwind/lang/en.json` or the GUI shows the raw key.
  `Validate()` clamps. Client edits never apply locally — they send `ConfigUpdateRequestPacket`; the server
  checks `controlserver` privilege, applies, persists, and rebroadcasts (singleplayer host is privileged).
- **Graceful degrade:** every Harmony patch resolves its target via `AccessTools` and logs + no-ops if the
  target/fields are missing, so a game update can't crash the mod (gameplay wind still works).

## Dependencies

- **Hard:** Algernons Terrain Sampler (`algernonsterrainsampler`, declared in `modinfo.json`). Server-only
  ModSystem (`TerrainSamplerMod.Instance`); orographic is therefore a server-side correction — client
  visuals use the base (non-orographic) field. Vendored at `libs/AlgernonsTerrainSampler.dll`.
- **Soft (optional):** ConfigLib (`configlib`, + its `vsimgui` dep) — in-game settings GUI. NOT in modinfo.
  All ConfigLib-typed code is isolated in `DynamicWindConfigLib` and only entered after
  `ModLoader.IsModEnabled("configlib")`, so a missing `configlib.dll` can't cause a type-load failure.
  Vendored at `libs/configlib.dll` (`<Private>False</Private>`; never shipped). VSImGui is a runtime-only
  transitive dep — not referenced at compile time.
- **Compat:** memzuuu's SailboatHotkeys (`sailboathotkeys`) uses no Harmony and only sets the vanilla
  `sailPosition` attribute, which our sailboat patch reads — they coexist with no special handling.

## Harmony patch targets (all guarded)

- `Vintagestory.GameContent.CloudRenderer.CloudTick` and `FluffyClouds.CloudRendererMap.CloudTick` (client) —
  fields `cloudSpeedX/cloudSpeedZ` (+ `targetCloudSpeedX/Z` where present), patched independently so either
  cloud-quality setting still follows the wind.
- `Vintagestory.GameContent.Mechanics.BEBehaviorWindmillRotor.CheckWindSpeed` (server) — field `windSpeed`,
  `ownFacing` on base `BEBehaviorMPRotor`. Sets `windSpeed` to the |alignment| magnitude and records the
  alignment sign per rotor.
- `Vintagestory.GameContent.Mechanics.BEBehaviorMPRotor.GetTorque` (server) — prefix/postfix flips the
  rotor's `propagationDir` for wind-reversed windmills so the `num` sign inverts: the rotor drives the
  network backwards AND the vanilla speed-governor branch applies in reverse (capping the speed). Negating
  the torque result instead would stall or, in the braking branch, accelerate without limit. Alignment is
  taken against the perpendicular of `ownFacing` (the sail-disc faces across the axle).
- `Vintagestory.GameContent.EntityBoat.SeatsToMotion` (both sides) — also `EntityShipwrightBoat` if present.
  Genuinely both sides: `EntityBoat` drives it from `OnRenderFrame` client-side for prediction and from
  `OnGameTick` server-side, and trim makes any divergence visibly rubber-band.
- **No patch needed for sail trim itself** — `EntityBoat.OnInteract` already walks `SidedProperties.Behaviors`
  and calls each one, so `EntityBehaviorSailTrim.OnInteract` picks up the clew-knot clicks for free.
- **No patch needed for maple seeds / dropped items** — vanilla `EntityItem.OnGameTick` already adds
  `GetWindSpeedAt` to motion (scaled by `1000/MaterialDensity`); our hook makes that directional for free.

## Repo / workflow

Local git repo (iterating locally; not pushed anywhere yet). Conventions:
- The vendored `libs/` DLLs are **git-ignored, not committed** (third-party binaries). A fresh clone must
  repopulate them before building — they are compile-time only (`<Private>False</Private>`) and never shipped:
  ```bash
  mkdir -p libs
  unzip -o -j .../configlib_1.12.0.zip configlib.dll -d libs
  unzip -o -j .../algernonsterrainsampler_1.3.0.zip AlgernonsTerrainSampler.dll -d libs
  ```
  (Source zips live in the player's `VintagestoryData/Mods/` once installed.)
- `bin/`, `obj/`, `Releases/` (Cake package output), and `libs/` are git-ignored. The `.gitignore` is the
  standard C#/VisualStudio/VSCode/Rider template plus the `Releases/` and `libs/` lines.
- Commits are SSH-signed (the global git config enables `commit.gpgsign`); never bypass signing.

## Build / install

- `dotnet build` (the `VINTAGE_STORY` env var points at the game install; refs resolve from there +
  `$(VINTAGE_STORY)/Mods/` + `$(VINTAGE_STORY)/Lib/`, plus vendored `libs/`). Output: `bin/Debug/Mods/mod/`.
- Release/package: `./build.sh` (Cake) produces a zip under `Releases/`.
- Install: copy the zip into `../../VintagestoryData/Mods/` (next to `algernonsterrainsampler_*.zip`),
  or run the game via the launch profile in `Properties/launchSettings.json`.
- `tools/keyframegen/` — standalone console tool (own `.csproj`, NOT part of `DynamicWind.sln`/the mod
  build) that bakes the sailboat's `sailtrim` clew-rope keyframes; see "Clew ropes" above. Run with
  `dotnet run -- <fully-patched sailed.json> <output.json>` from that directory (needs the same
  `VINTAGE_STORY` env var), then splice the branch objects in the output into `patches/boat-sailed-shape.json`'s
  `sailtrim` animation. Regenerate whenever the boat shape or the clew-rope shape patch changes.

## Decompiling the game (when XML docs are insufficient)

`ilspycmd` (`~/.dotnet/tools/ilspycmd`). **The real game-content DLLs are in `$(VINTAGE_STORY)/Mods/`**
(`VSEssentials.dll`, `VSSurvivalMod.dll`) — the copies in the install root are stubs. API + Lib DLLs are
in the install root. Example:
`ilspycmd "$VINTAGE_STORY/Mods/VSEssentials.dll" -t Vintagestory.GameContent.CloudRenderer`

## Known limitations / future work

- Orographic affects server-side wind only (sampler is server-side); client cloud/particle visuals use the
  base field — a minor magnitude divergence near mountains. A coarse-grid broadcast could close it.
- `WindAt` + orographic samples on every server `GetWindSpeedAt`; if profiling shows cost, memoize the
  terrain height by coarse tile.
- Sailboat tacking (upwind gain by zig-zag) is intentionally not implemented — trim, not tacking, is the
  mechanic. Sailing dead upwind is meant to be slow; furl and row instead.
- The boat shape patch (`patches/boat-sailed-shape.json`) uses **positional** JSON-pointer paths
  (`/elements/0/children/3/...`), so another mod that structurally rewrites the same shape ahead of us
  would land our clew rig in the wrong place. Regenerate it against the current vanilla `sailed.json` if
  the boat model ever changes; the furled clew geometry was ported from a donor model by rebasing
  model-space positions by `−(8,0,8)` into vanilla's `SailFurled` frame. The donor also carried
  `"shade": false` on every added element (flat, unshaded); vanilla's own rope/block geometry never sets
  `shade` at all (normal AO shading), so the ported boxes rendered visibly flat next to it — stripped on
  import to match.
