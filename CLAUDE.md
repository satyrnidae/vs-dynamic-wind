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
  deliberately measured with the terrain-thermal sea-breeze gradient (see "Terrain thermal pressure"
  below) EXCLUDED (`WindField.WindAt(..., includeTerrainGradient: false)`) — that gradient is real and
  still steers the wind's DIRECTION everywhere via the normal (terrain-included) `WindAt` call, but it's
  present near almost any coastline/terrain contrast regardless of whether a pressure system is nearby,
  so folding it into `frontExcess` would inflate average speed well outside any actual front. So a calm
  vanilla pattern can't zero the wind globally, fronts still blow when vanilla is calm, and an area with
  no active pressure cell falls back to vanilla's (possibly near-zero) speed — local calm is fine, global
  night-death is not.
- **Clouds** — Harmony postfix on both `CloudRenderer.CloudTick` and `FluffyClouds.CloudRendererMap.CloudTick`
  (whichever is active for the player's cloud-quality setting) rotates the cloud-drift vector to the wind bearing.
- **Windmills** — Harmony postfix on `BEBehaviorWindmillRotor.CheckWindSpeed`: power × cos(angle between
  wind and rotor axle). Head-on = full, perpendicular = stalled, from-behind = negative → spins backwards.
  Both the wind's speed and its alignment are eased per-rotor toward each tick's raw reading
  (`WindmillDirectionality.SpeedEaseRatePerSecond`) rather than snapped — real sails have rotational
  inertia — and reversal is decided from the EASED alignment too, so a shifting wind eases the mill down
  through a stall and back up the other way instead of flipping outright.
- **Sailboats** — Harmony postfix on `EntityBoat.SeatsToMotion`: tailwind +50%, crosswind base,
  headwind −10% (piecewise by heading-vs-wind alignment). Tacking is out of scope.
- **Weather** — `WeatherCoupling` (Harmony postfix on `WeatherSystemBase.GetRainCloudness`) raises
  cloudness/rain inside lows and lowers it in highs — storms form in **low** pressure, highs are
  fair-weather subsidence, matching real synoptic charts (see [noaa.gov/jetstream/wxmaps](https://www.noaa.gov/jetstream/wxmaps)).
  Lows also add circulation, so storms = stronger, swirlier wind.
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
  Each is tilted off the pure travel/anti-travel axis toward its real-world side by `FrontComputer.WaveFrontTiltAngle`
  (warm leads AND tilts poleward — where warm air overruns; cold trails AND tilts equatorward — where cold
  air digs in), via `WindField.PolewardZSign`/real latitude — giving the classic asymmetric "comma" shape
  instead of two lines sitting exactly opposite each other through the low's centre. Neither line ever
  splits through the low's middle or starts at a single point: each IS that side's own elliptical EDGE
  (`EllipseGeometry.RadiusToward`, accounting for `RadiusMinor`/`Orientation`) traced across a wide arc
  (`FrontComputer.BuildLeadingEdgeArc`, ±`ArcHalfAngle` around its own bias angle — a cap hugging the whole
  front-facing side of the low), with that boundary radius scaled outward by ITS OWN reach
  (`WarmFrontReach`/`ColdFrontReach` respectively) — nothing more. Two earlier approaches were tried and
  discarded: an explicit Bezier JOIN connecting a front's curve to a neighbouring low's, which could
  self-intersect or collapse to near-zero length when several systems sat close together (rendering as
  tiny, wrong-colored blobs); and, once cells grew to synoptic scale, a free march trailing outward from
  the arc's ends (gently deflected by nearby cells' background pressure gradient) plus a sine "wobble" for
  organic texture, which read as jagged/noisy rather than a smooth boundary at that size. Both were removed
  in favour of the plain elliptical arc — matching how a real synoptic chart traces a front along the
  system's own boundary, not an arbitrary curve trailing away from it. Stationary and Occluded fronts still
  render only a SINGLE line (a real stationary/occluded front is one boundary, not a warm/cold pair),
  picking their bias angle FROM a nearby cell's direction (bearing-to-neighbour / catch-direction
  respectively) but never drawing an explicit connecting curve to it. `WindField.StormBiasAt` and
  `FrontRegimeBiasAt` bias precipitation/storm-intensity toward EXACTLY the same edges each low's front
  lines actually draw — occluded: the single catch/equatorward axis; stationary: bearing to its nearest
  other significant cell; moving: both the leading/warm and trailing/cold axes from the shared
  `WindField.MovingFrontAngles` (also what `FrontComputer` calls to draw the two lines) — rather than an
  independently-chosen geography split that can silently drift out of sync with what's drawn (as the old,
  purely poleward/equatorward convention did once fronts switched to leading/trailing a low's own
  direction of travel). `StormBiasAt`'s along-axis falloff is a smooth (not hard-clamped) ramp
  (`SmoothPositive`) scaled by cell radius, so precipitation doesn't crease sharply exactly through a low's
  centre — highs contribute nothing to either function.
- **Isobars** — rendered in the SAME layer as fronts (`FrontMapLayer`, not a separate map layer), computed
  alongside markers/fronts in the same `FrontComputer.Compute` call. Unlike fronts (lows only), isobars are
  drawn around EVERY significant cell, highs included — a real chart draws them around anticyclones too.
  A single cell's own pressure contribution is exactly `Strength * exp(-norm2)`
  (`WindField.CellPressureAt`), and `norm2 == 1` exactly at `EllipseGeometry.RadiusToward`'s boundary, so
  the contour at any level `L < |Strength|` is that same ellipse uniformly scaled by
  `sqrt(ln(|Strength| / L))` (`FrontComputer.BuildIsobarRing`) — no marching squares needed, and (like the
  front-line arc) this ignores neighbouring cells' contribution to the shape. Rings are spaced at a FIXED
  absolute pressure step (`FrontComputer.IsobarStep`), real-chart style: a weak system shows one or two
  widely-spaced rings, a strong one shows many tightly-packed ones — that density variation is a feature,
  not something to normalize away. `FrontMapLayer` draws them first/furthest back (faint, alpha fading per
  ring outward from the innermost) so they read as background context behind the front lines/markers.
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
- **Heat/cold waves** (`WindFieldSimulation.TrySeedHeatColdWave`, config `HeatColdWaveEnabled`) — a rare,
  very strong, near-stationary `PressureCell.IsHeatColdWave` HIGH sited on a genuine terrain-thermal
  hotspot, restricted to water/ocean sites (`TerrainThermal.IsWater`, same siting restriction as ordinary
  thermal seeding below — land hotspots no longer seed mobile cells, only the static terrain baseline),
  always anticyclonic regardless of the hotspot's own (now seasonal — see "Terrain thermal pressure" below)
  sign — real heat domes and arctic-high cold snaps are both highs; it's the trapped, subsiding air
  itself that reads hot or cold. Heat wave vs. cold wave is decided by the season at the spawn site (warm
  months → heat wave, cold months → cold wave), NOT by the hotspot's sign, and both draw from the *same*
  rare spawn-attempt rate (`HeatColdWaveRatePerDay`, ~once per ~33 game-days), so — since seasons split
  roughly 50/50 warm/cold over a year — hot and cold waves come out equally rare by construction rather
  than needing separate rates. Pushes local temperature 5–10°C outside the seasonal average via
  `PressureCell.WaveTemperatureDelta` / `WindField.HeatColdWaveDeltaAt`, added in `OnGetClimate` alongside
  (not blended into) the normal `AirMassTemp` term — an ordinary front sitting nearby would otherwise
  dilute the wave's swing back toward neutral. Spawned at near-max `Strength` and (like a thermal-seeded
  cell) stationary at birth: that large initial magnitude is what gives it "a lot of inertia" — the same
  proportional dispersal decay every cell gets takes far longer to fade a near-max-strength cell below
  `MinStrength`, so it naturally persists on the order of days rather than hours, with a 3–5 day
  `DiesTotalHours` as the outer backstop. It also eases toward the local background wind far slower than
  an ordinary cell (`HeatColdWaveVelocityEaseRatePerHour`), so only a strong, moving system passing nearby
  will meaningfully nudge it before its natural lifetime runs out. Ordinary terrain-thermal seeding
  (`ThermalSeedRatePerDay`) was also made rarer (14/day → 4/day) — it was cluttering the field with far
  too many short-lived cells.
- **Terrain thermal pressure** (`TerrainThermal`) — beyond the moving synoptic cells, pressure has a
  terrain-driven baseline: warm, sun-exposed dry land → low (rising air), always. Water's sign is
  **seasonal**, matching real air/water thermal contrast: warm-season air over cool water is stabilizing →
  high (sinking air, the marine-layer/sea-breeze pattern); cold-season air over relatively warm water is
  destabilizing → low (real nor'easters, polar lows, autumn/winter lake-effect systems) — both are genuine
  synoptic phenomena, blended continuously by season (`TerrainThermal.SeasonWarmthAt`, same cosine pattern
  as `WindFieldSimulation`'s other seasonal-warmth lookups) so shoulder seasons taper toward neutral rather
  than flipping outright. Modulated by time of day + temperature. Water's pressure magnitude scales with
  **depth** (sea level − `GetTerrainMapheightAt` seabed, full at `WaterFullDepth`≈20 blocks) so shallow
  coasts read near-neutral. Added into `WindField.PressureAt` via `ThermalPressureFunc` (set per side: each
  samples its own world's surface height + `GetClimateAt`/season). Only a fraction (`ThermalStaticScale`,
  default 0.35) stays as a static terrain-pinned baseline; the rest of the terrain signal is expressed as
  **mobile seeded cells** (below) so the pressure differential drifts with the wind instead of being glued
  to the ground. This is what gives the minimap spatial pressure variation (blue land / crimson water, now
  shifting seasonally over water), and biases storms toward hot low-pressure land. `TerrainThermal.IsWater`
  is a separate, sign-independent land/water check (used by the mobile-cell siting gates below) since
  water's own pressure sign can no longer be used to infer land vs. water — there is no ocean-vs-lake
  distinction anywhere in the codebase, so "over water" is the closest available proxy for "over ocean".
  NOTE: `WeatherCoupling.OnGetClimate` calls `PressureAt` → thermal → `GetClimateAt`,
  so it uses a `[ThreadStatic]` reentrancy guard to avoid infinite recursion. (Thermal currently feeds
  pressure/weather/visualization only, not wind direction — sea-breeze wind coupling is possible future work.)
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
    falls back to the fixed jetstream velocity (or, for a thermal-seeded cell, stays stationary — see below).
  - **Dispersal** — `DispersalRate` grows each cell's radius and decays its strength per hour; cells expire
    once strength falls below `MinStrength` (or hit their lifetime backstop). So systems spread out and fade.
  - **Bernoulli** — `BernoulliStrength`: faster-than-nominal flow lowers a cell's pressure (toward low, +),
    stagnant air raises it. Sampled once per tick from the same background wind (no recursion).
  - **Thermal seeding** — `ThermalSeeding` spawns small mobile cells from terrain thermal hotspots,
    restricted to water/ocean sites (`TerrainThermal.IsWater`) — land hotspots no longer seed mobile cells,
    only the static terrain-pressure baseline above. Water's sign is seasonal (see "Terrain thermal
    pressure" above): warm-season water seeds a HIGH, cold-season water seeds a LOW. Unlike a regular
    jet-spawned system, these are born perfectly STATIONARY (a hotspot itself isn't moving) and only pick up
    speed gradually via the Advection easing above as nearby cells' circulation and the base jetstream act
    on them, then disperse like any other cell.
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
  AND the terrain thermal contrast (water→land sea breeze) actually push the air. `WindField.WindAt` adds
  `PressureWindStrength · ∇P` (numeric gradient of `PressureAt`, clamped). Runs on both sides.
- **Terrain (orographic)** — in `WindField.WindAt` on BOTH sides via a per-side `TerrainHeightFunc`
  (server: Algernons Terrain Sampler at any coord; client: rain-map height for loaded chunks, so the minimap
  shows it). Wind climbing into a slope slows (stagnation) and curves along the contour around the obstacle;
  wind sheltered by higher terrain upwind slows in the lee wake. Tunables: `OrographicSlowdownFactor`,
  `OrographicDeflectionStrength`, `OrographicWakeFactor`.
- The thermal-pressure and terrain-height lookups are wrapped in `ThermalCache` (coarse tile + time bucket,
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
synoptic-scale systems) rather than the server's loaded-chunk extent: regular jet-spawned systems
(`WindFieldSimulation.Spawn`) get full-range variety, while thermal seeds and heat/cold waves each sample a
quarter-width sub-band (`SpawnRadiusSubBandFraction`) near the low/high end of that range respectively, so
they stay visibly smaller/larger than a full system even as the config range is retuned. New cells spawn
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
- Sailboat tacking (upwind gain by zig-zag) is intentionally not implemented.
