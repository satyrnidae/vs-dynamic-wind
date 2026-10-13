# AGENTS.md — Dynamic Wind for Vintage Story

Agent onboarding for this repo. This file is written for **automated coding agents (LLMs)** working in the repository, not human contributors. It gives a compact overview of how to build, where things live, and what the mod does at a glance. The *why* behind every decision — the deep rationale, per-feature math, and tradeoffs — lives in [`CLAUDE.md`](./CLAUDE.md); read that when you need depth.

Vintage Story **1.22.3** code mod.

## Build toolchain

- Requires the `VINTAGE_STORY` environment variable pointing at a Vintage Story install; refs resolve from there plus vendored `libs/`.
- Build: `./build.sh` (or `.\build.ps1`) runs Cake — effectively `dotnet run --project ./ZZCakeBuild/CakeBuild.csproj "$@"`. Pass the same args you'd pass to Cake.
- A fresh clone must repopulate the git-ignored vendored DLLs in `libs/` before building:

  ```bash
  mkdir -p libs
  unzip -o -j .../configlib_1.12.0.zip configlib.dll     -d libs
  unzip -o -j .../algernonsterrainsampler_1.3.0.zip      AlgernonsTerrainSampler.dll -d libs
  ```

- Output: `DynamicWind/bin/Debug/Mods/mod/`. Package a release with the Cake build (`Releases/`).
- `tools/keyframegen/` is a standalone console tool (own `.csproj`, not in the mod build). It bakes the sailboat's clew-rope animation keyframes. Run it with `dotnet run -- <input.json> <output.json>` from that directory; splice its branch objects into `patches/boat-sailed-animations.json`. Needs the same `VINTAGE_STORY` env var.

## Structure

Top-level entries:

- `DynamicWind.sln` — the mod solution (builds `DynamicWind/DynamicWind.csproj`).
- `DynamicWind/` — the mod itself: `src/` (code), `assets/`, `Properties/`, `modinfo.json`.
- `ZZCakeBuild/` — the Cake build project.
- `tools/keyframegen/` — the standalone sailboat keyframe-baking console tool.

## Architecture

Three-class ModSystem split:

- `DynamicWindModCommon` (`ShouldLoad => false`) — loads/validates config, registers the single `"dynamicwind"` network channel + message types, owns the shared `WindField` and the `OnGetWindSpeed` hook (with a virtual orographic step).
- `DynamicWindModServer` (`ShouldLoad => side == EnumAppSide.Server`) — authoritative config + field source; syncs both to clients, applies server Harmony patches (windmill, sailboat) plus weather coupling, runs `/dwind`. The heavy `WindFieldSimulation` runs on its own dedicated thread and ticks independently of the main tick.
- `DynamicWindModClient` (`ShouldLoad => side == EnumAppSide.Client`) — mirrors config + field, runs the hook, patches the cloud renderer, wires the optional ConfigLib GUI.

Core idea: `WindField.WindAt(x, z, totalHours)` is a **deterministic** function of a small state (base params + pressure-cell list). The server simulates and broadcasts only that state (`WindFieldState`); the client re-evaluates the same function locally — no per-block wind streaming. `DynamicWindRuntime` is a static bridge so Harmony patches can reach the per-side field/config.

## Conventions & dependencies

- **Config** (`DynamicWindConfig`, `src/Config/DynamicWindConfig.cs`) — server-authoritative, protobuf-synced; the same class is the JSON config and the network packet (public auto-properties). `[Description]/[Range]/[Category]` attributes drive the optional ConfigLib GUI. All settings are **flat top-level scalars** because ConfigLib's managed-config reflection only sees top-level scalar properties (it does not recurse into nested objects), so flattening makes every knob appear in the GUI. Each setting needs a `setting-<PropertyName>` lang key or the GUI shows the raw key. The surface is deliberately small: 16 feature toggles + `MaxActiveCells` (its one `[Range]`). Every other tunable strength/rate/angle/threshold is a hardcoded constant at its point of use — check the actual property list before assuming a named quantity is user-editable. Client edits never apply locally; they send a config request, and the server checks `controlserver` privilege before applying/persisting/rebroadcasting.
- **Graceful degrade:** every Harmony patch resolves its target via `AccessTools` and logs + no-ops if the target/fields are missing, so a game update can't crash the mod (vanilla gameplay wind still works).
- **Hard dependency — Algernons Terrain Sampler** (`algernonsterrainsampler`, in `modinfo.json`). Server-only; orographic is therefore a server-side correction and client visuals use the base field. Vendored at `libs/AlgernonsTerrainSampler.dll`.
- **Soft (optional) — ConfigLib** (`configlib` + its `vsimgui` dep). In-game settings GUI, NOT in modinfo. All ConfigLib-typed code is isolated in `DynamicWindConfigLib` and only entered after `ModLoader.IsModEnabled("configlib")`, so a missing `configlib.dll` can't cause a type-load failure. Vendored at `libs/configlib.dll` (`<Private>False</Private>`; never shipped).
- **Soft (optional) — Immersive Body Temperature** (`warmweathereffects`) — humidity compat only, gated behind both `ModLoader.IsModEnabled(...)` and config `HumidityBodyTempCompatEnabled`. Reached entirely through reflection so a missing/updated DLL can't break the build. Server-only.
- **Compat — memzuuu's SailboatHotkeys** (`sailboathotkeys`) uses no Harmony and only sets the vanilla `sailPosition` attribute, which our sailboat patch reads; they coexist with no special handling.

## What this mod does

Vanilla wind is a scalar that always blows due East (+X). This mod gives wind a real, slowly-evolving **direction** driven by a west-to-east jetstream plus moving low/high pressure systems, and couples the rest of the world to it:

- **Particles / foliage sway / blowing snow / dropped items / ground-movement wind** — follow the true wind vector for free via an `OnGetWindSpeed` hook that rewrites the wind's X/Z. One exception is **rain**, which vanilla doesn't keep wind-aligned in flight and so needs a direct field tweak (see `CLAUDE.md`).
- **Wind speed is additive, not multiplicative** — vanilla's own calm-baseline speed is kept as the floor; front wind from active pressure cells is added on top. See `CLAUDE.md` for why the terrain sea-breeze gradient is excluded from that excess.
- **Clouds** — drift vector rotated to the wind bearing via a Harmony postfix on the cloud renderer(s).
- **Windmills** — power scales with `cos(angle between wind and rotor axle)`; reversal eases through zero rather than snapping, with a perpendicular dead-band. See `CLAUDE.md`.
- **Weathervanes / rotorvanes** — decorative floor blocks that read the same directional field as everything else (shared base classes for the two variants). See `CLAUDE.md`.
- **Sailboats** — a trimmable sail whose thrust comes from how the wind meets the sail, with an animated clew-rope rig baked offline by `tools/keyframegen`. See `CLAUDE.md` for the full thrust model. Tacking is intentionally out of scope.
- **Pressure cells / jetstream** — synoptic-scale low/high systems that advect and interact; the basis for fronts, highs, and lows.
- **Weather coupling, humidity, map layers, Doppler radar** — tie wind to weather, add a humidity model, and expose direction/speed overlays.

For per-feature math (clew-rope solve, sail trim curve, sea-breeze exclusion, etc.), read `CLAUDE.md`.

## Axis convention

`+X = East`, `+Z = South` (so North = −Z, West = −X). Jetstream base flow = +X. Bearing in `/dwind debug` is `atan2(Z, X)` degrees: 0 = E, 90 = S, ±180 = W, −90 = N. Because the map is north-up with +Z pointing down (south), a screen counter-clockwise tangent is `(rz, −rx)`, so lows circulate **counter-clockwise** and highs **clockwise** (Northern-Hemisphere convention).
