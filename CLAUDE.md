# CLAUDE.md — Dynamic Wind

Guidance for Claude Code when working in this repo. Vintage Story **1.22.3** code mod.

## What this mod does

Vanilla wind is a scalar that always blows due **East (+X)**. This mod gives wind a real,
slowly-evolving **direction** driven by a west-to-east jetstream plus moving low/high pressure
systems, and couples the rest of the world to it:

- **Particles, foliage sway, blowing snow, dropped items (incl. maple seeds), ground-movement wind** —
  follow the true wind vector *for free*, because vanilla routes them through `GetWindSpeedAt` and we
  rewrite that vector's X/Z in an `OnGetWindSpeed` hook. No patches for these.
- **Clouds** — Harmony postfix on `CloudRenderer.CloudTick` rotates the cloud-drift vector to the wind bearing.
- **Windmills** — Harmony postfix on `BEBehaviorWindmillRotor.CheckWindSpeed`: power × cos(angle between
  wind and rotor axle). Head-on = full, perpendicular = stalled, from-behind = negative → spins backwards.
- **Sailboats** — Harmony postfix on `EntityBoat.SeatsToMotion`: tailwind +50%, crosswind base,
  headwind −10% (piecewise by heading-vs-wind alignment). Tacking is out of scope.
- **Weather** — `OnGetClimate` hook raises rainfall inside lows (storms form in low pressure) and lowers
  it in highs. Lows also add circulation, so storms = stronger, swirlier wind.
- **Terrain thermal pressure** (`TerrainThermal`) — beyond the moving synoptic cells, pressure has a
  terrain-driven baseline: warm, sun-exposed dry land → low (rising air), cool water → high (sinking air),
  modulated by time of day + temperature. Water high pressure scales with **depth** (sea level −
  `GetTerrainMapheightAt` seabed, full at `WaterFullDepth`≈20 blocks) so shallow coasts read near-neutral. Added into `WindField.PressureAt` via `ThermalPressureFunc` (set
  per side: each samples its own world's surface height + `GetClimateAt`). Only a fraction
  (`PressureDynamics.ThermalStaticScale`, default 0.35) stays as a static terrain-pinned baseline; the rest
  of the terrain signal is expressed as **mobile seeded cells** (below) so the pressure differential drifts
  with the wind instead of being glued to the ground. This is what gives the minimap
  spatial pressure variation (blue land / crimson water), and biases storms toward
  hot low-pressure land. NOTE: `WeatherCoupling.OnGetClimate` calls `PressureAt` → thermal → `GetClimateAt`,
  so it uses a `[ThreadStatic]` reentrancy guard to avoid infinite recursion. (Thermal currently feeds
  pressure/weather/visualization only, not wind direction — sea-breeze wind coupling is possible future work.)
- **Pressure dynamics** (`PressureDynamicsConfig`, evolved in `WindFieldSimulation`) — cells are a living
  field, not static gaussians:
  - **Advection** — each server tick a cell drifts along the *local background wind* (`WindAt(x,z,t,excludeCell)`,
    self excluded), and its current velocity is stored so the client extrapolates linearly between snapshots.
    `AdvectionSpeed` (blocks/game-hour per unit wind). Off → falls back to the fixed jetstream velocity.
  - **Dispersal** — `DispersalRate` grows each cell's radius and decays its strength per hour; cells expire
    once strength falls below `MinStrength` (or hit their lifetime backstop). So systems spread out and fade.
  - **Bernoulli** — `BernoulliStrength`: faster-than-nominal flow lowers a cell's pressure (toward low, +),
    stagnant air raises it. Sampled once per tick from the same background wind (no recursion).
  - **Thermal seeding** — `ThermalSeeding` spawns small mobile cells from terrain thermal hotspots near
    players (hot dry land → low, deep water → high), which then advect + disperse.
  - **Altitude lapse** — `WindField.PressureAt(x,y,z,t)` adds a vertical term: real air pressure falls with
    height, read as "more low" (+), reaching +`AltitudePressureDrop` (default 0.5 = −50%) at the build ceiling,
    0 at/below sea level. Vertical-only, so it never alters wind — only the displayed/weather-coupled pressure
    (map tint, `WeatherCoupling`, `/dynamicwind debug` all pass their Y). `SeaLevel`/`WorldHeight` set per side.
- **Pressure-gradient wind** — wind bleeds down the pressure gradient (high → low), so the synoptic cells
  AND the terrain thermal contrast (water→land sea breeze) actually push the air. `WindField.WindAt` adds
  `PressureWindStrength · ∇P` (numeric gradient of `PressureAt`, clamped). Runs on both sides.
- **Terrain (orographic)** — in `WindField.WindAt` on BOTH sides via a per-side `TerrainHeightFunc`
  (server: Algernons Terrain Sampler at any coord; client: rain-map height for loaded chunks, so the minimap
  shows it). Wind climbing into a slope slows (stagnation) and curves along the contour around the obstacle;
  wind sheltered by higher terrain upwind slows in the lee wake. Tunables in `OrographicConfig`
  (`SlowdownFactor`, `DeflectionStrength`, `WakeFactor`).
- The thermal-pressure and terrain-height lookups are wrapped in `ThermalCache` (coarse tile + time bucket,
  thread-safe) because `WindAt` samples them several times per call on a hot path; this also keeps the
  weather-coupling re-entry rare.
- **Wind overlay** (`WindArrowMapLayer`) — a fixed 24×24 grid of arrows on the **full world map only**
  (skips the minimap HUD via the `GuiElementMap.snapToPlayer` flag). Arrow length scales **logarithmically**
  with wind speed so low speeds stay legible.
  Each samples `GetWindSpeedAt` at its cell's world midpoint (player Y), rotates to the bearing, and scales
  its length by wind speed (~10% at 1% speed → 100% at full; hidden at 0%). The greyscale arrow art is tinted
  by pressure (from the synced field's `PressureAt`): low → sky blue `#66C4FF`, high → crimson `#FF4D35`,
  neutral → white. Fixed screen grid → zooming out makes each arrow cover more world distance. Registered via
  `WorldMapManager.RegisterMapLayer`; arrow art at `assets/dynamicwind/textures/icons/winddir.png`.
  Resampled ~7×/s and cached for perf.

## Axis convention

`+X = East`, `+Z = South` (so North = −Z, West = −X). Jetstream base flow = +X. Bearing in
`/dynamicwind debug` is `atan2(Z, X)` degrees: 0 = E, 90 = S, ±180 = W, −90 = N.

## Architecture

Three-class ModSystem split (modelled on `D:\satyrnidae\vs-balladeer-class`):

- `DynamicWindModCommon` (`ShouldLoad => false`) — loads/validates config, registers the single
  dedicated `"dynamicwind"` network channel + message types, owns the shared `WindField` and the
  `OnGetWindSpeed` hook (with a virtual orographic step).
- `DynamicWindModServer` (`ShouldLoad => Server`) — runs `WindFieldSimulation`, is the authoritative
  config + field source, syncs both (on `PlayerJoin` and on change), applies the server Harmony patches
  (windmill, sailboat) + `WeatherCoupling`, orographic via the sampler, and the `/dynamicwind` command.
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

- `Vintagestory.GameContent.CloudRenderer.CloudTick` (client) — fields `cloudSpeedX/cloudSpeedZ`.
- `Vintagestory.GameContent.Mechanics.BEBehaviorWindmillRotor.CheckWindSpeed` (server) — field `windSpeed`,
  `ownFacing` on base `BEBehaviorMPRotor`.
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
