# Dynamic Wind

A [Vintage Story](https://www.vintagestory.at/) mod that replaces the game's constant
due-east wind with a real, slowly-evolving wind field driven by a west-to-east jetstream
and moving low/high pressure systems, plus everything downstream of wind that comes
along for the ride: clouds, weather, windmills, and sailboats.

Built for Vintage Story **1.22.3**.

## AI Usage Disclosures
- **This mod was developed with the use of an LLM.**
  - Alternatives are available, including [Wind Vanes & Randomized Winds](https://mods.vintagestory.at/windvanes) and [Pelagus Winds](https://mods.vintagestory.at/pelaguswinds) ([or its fork for 1.22.3](https://mods.vintagestory.at/show/mod/52907))
- **Visual Assets** (map icons, models, animations) were not created using artificial intelligence
  - Clew line stretch animation is dynamically generated using code written with the use of an LLM
- Mod is CC0/Public Domain as a result of AI involvement

## What it does

Vanilla wind is a scalar that always blows due east. This mod gives it a real direction
and couples the rest of the world to it:

- **Direction & speed**: a jetstream base flow plus moving pressure cells (highs and
  lows) determine the true wind vector everywhere. Wind speed is *additive*: vanilla's
  own natural speed variation stays as a baseline, and pressure fronts add on top of it,
  so calm weather never goes globally dead and fronts still blow through when the
  baseline is quiet.
- **Particles, foliage sway, blowing snow, dropped items (including maple seeds), and
  ground-movement wind** all follow the true wind vector automatically; vanilla already
  routes these through the game's wind query, so no extra patches are needed for them.
- **Clouds** drift toward the wind bearing instead of a fixed direction.
- **Windmills** generate power based on the angle between the wind and the rotor axle.
  Head-on is full power, crosswind stalls it, and a wind from behind spins the rotor
  backwards.
- **Sailboats** get a real speed bonus running with the wind, a penalty running against
  it, and something in between on a crosswind. Tacking upwind is not modeled.
- **Weather** responds to the pressure field: storms and rain form in low-pressure
  systems, highs bring fair weather, matching real synoptic charts. Moving lows also
  render warm and cold fronts on the map, split by real hemisphere/latitude geography,
  including front occlusion as a fast-moving cold front catches up to another system.
- **Terrain** shapes both pressure (sun-warmed land vs. cool water drives local pressure
  differences, giving the map real spatial variation) and wind itself (orographic
  slowdown climbing a slope, deflection around obstacles, and lee-side wake).
- **Real latitude & seasons**: the wind field uses the game's actual (repeating)
  climate latitude, not a naive map-center assumption, and the jetstream tilts
  poleward in summer and equatorward in winter like a real hemisphere.
- **Wind map overlay**: the full world map shows a grid of arrows indicating wind
  direction and relative speed, tinted by local pressure (crimson = low, cobalt = high).

The simulation is server-authoritative: the server runs the pressure-cell simulation and
syncs a small amount of state to clients, who evaluate the same deterministic wind
function locally rather than streaming per-block wind data.

## Installation

1. Install the required dependency: [Algernon's Terrain Sampler](https://mods.vintagestory.at/algernonsterrainsampler).
2. Grab the latest Dynamic Wind release and drop the zip into your `Mods` folder
   (alongside the Terrain Sampler mod), or use the in-game mod manager.
3. (Optional) Install [ConfigLib](https://mods.vintagestory.at/configlib) for an
   in-game settings GUI. Dynamic Wind works fine without it - settings can still be
   edited directly in its JSON config file.

## Configuration

All settings live in the mod's server-side JSON config and are synced to clients. With
ConfigLib installed, every setting is also exposed in an in-game GUI, grouped by
category. Without it, edit the config file directly and restart or use the mod's admin
command to apply changes (requires the `controlserver` privilege on a multiplayer
server; the singleplayer host always has it).

Notable tunables include jetstream strength, front wind strength, pressure-cell spawn
rate and sizing, orographic slowdown/deflection strength, seasonal jetstream tilt angle,
indoor/underground wind occlusion, and heat/cold wave frequency. See in-game tooltips
(via ConfigLib) or the config file's field names for the full list.

Use `/dynamicwind debug` in-game to inspect the current wind field state (bearing,
speed, seasonal bias, and more) at your position.

## Compatibility

- **Requires** Algernon's Terrain Sampler.
- **Works well with** ConfigLib (optional, for the settings GUI) and memzuuu's
  SailboatHotkeys.
- Every world-modifying patch resolves its target defensively and no-ops with a log
  message if it can't find what it expects, so a game update shouldn't be able to crash
  the mod outright (though its wind-direction and coupling features may silently stop
  applying until updated).

## Building from source

Requires the .NET SDK matching the game's target framework and a `VINTAGE_STORY`
environment variable pointing at your Vintage Story install (used to resolve game
references).

```bash
mkdir -p libs
# Vendored, compile-time-only dependencies (not shipped, not committed):
unzip -o -j path/to/configlib_*.zip configlib.dll -d libs
unzip -o -j path/to/algernonsterrainsampler_*.zip AlgernonsTerrainSampler.dll -d libs

dotnet build
```

Build output lands in `bin/Debug/Mods/mod/`. To produce a distributable zip, run
`./build.sh` (or `build.ps1` on Windows), which uses Cake to package a release under
`Releases/`.

## License

Released under [CC0 1.0](LICENSE), public domain. See [THIRD_PARTY](THIRD_PARTY) for
attribution of third-party assets (sound effects) bundled with the mod.
