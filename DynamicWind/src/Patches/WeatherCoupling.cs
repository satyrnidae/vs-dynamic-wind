using System;
using System.Reflection;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace DynamicWind.Patches;

/// <summary>
/// Couples the pressure field to REAL precipitation at the single chokepoint every consumer shares:
/// <see cref="WeatherSystemBase.GetRainCloudness"/>. A postfix shifts cloudness by local cell pressure
/// (+ = low -> wetter, - = high -> clearer, matching real fronts/subsidence), so falling rain, the
/// climate's Rainfall/RainCloudOverlay via <c>Event_OnGetClimate</c>, and the Doppler radar (reads
/// <c>GetPrecipitation</c>) all read one consistent field. Precipitation moves with wind because the
/// gating pressure cells advect.
///
/// Replaces the old <c>OnGetClimate</c>-event coupling, which only touched NowValues and never
/// affected <c>GetPrecipitation</c> (samples WorldGenValues) - radar showed rain where none fell. The
/// gate reads <see cref="WindField.CellPressureAt"/> only, never the climate/thermal path, so it can't
/// recurse into GetRainCloudness.
///
/// Patched once (static guard): in singleplayer the base method is shared by both weather-system
/// instances, and the postfix picks its side (and field) from <c>api.Side</c>.
/// </summary>
internal static class WeatherCoupling
{
    #region Constants

    // Precipitation is MULTIPLICATIVE, not additive: a low scales vanilla's own cloudness pattern up, a
    // high scales it down, reusing vanilla's organic broad-scale texture for free (patchy storm bands /
    // patchy clearing, no separate per-cell noise map needed - old CellWeatherNoise is gone). Also
    // sign-safe: a high can only ever scale cloudness DOWN, whereas subtracting zero-mean noise could
    // raise it over half a high's footprint.
    //
    // exp() keeps the result strictly positive and makes low/high exact reciprocals: pressure +p
    // multiplies by k, -p divides by the same k.
    private const double PrecipLogGain = 0.55;  // exponent per unit of (clamped) pressure
    private const double PressureClamp = 1.5;

    // Deliberately NO upper clamp: GetPrecipitation is Max(0, cloudness - 0.5) with no ceiling, so
    // capping cloudness at 1 here would silently cap precipitation at 0.5 and flatten every storm core
    // (and the radar's red band). Vanilla already clamps where it needs to (Event_OnGetClimate clamps
    // into RainCloudOverlay/Rainfall) - headroom above 1 is what makes an intense low read as intense.

    // Front-type intensity, matching real synoptic behaviour: occluded > cold > warm > stationary.
    // Scales the LOW side of the exponent below via WindField.FrontRegimeBiasAt, which is itself
    // StormLikelihood-weighted so an immature low hasn't earned full intensity yet.
    private const double OccludedPrecipMult = 1.4;
    private const double ColdPrecipMult = 1.2;
    private const double WarmPrecipMult = 0.7;
    private const double StationaryPrecipMult = 0.8;

    // Former DynamicWindConfig tunable, now a fixed constant (see DynamicWindConfig's class remarks).
    private const double StormCouplingStrength = 1.0;

    private static bool patched;

    #endregion

    #region Patch Application

    public static void Apply(Harmony harmony, ILogger logger)
    {
        if (patched) return;
        MethodInfo? target = AccessTools.Method(typeof(WeatherSystemBase), "GetRainCloudness");
        if (target == null)
        {
            logger.Warning("[dynamicwind] WeatherSystemBase.GetRainCloudness not found - precipitation coupling disabled.");
            return;
        }
        harmony.Patch(target,
            prefix: new HarmonyMethod(typeof(WeatherCoupling), nameof(DriftPrefix)),
            postfix: new HarmonyMethod(typeof(WeatherCoupling), nameof(Postfix)));
        patched = true;
        logger.Notification("[dynamicwind] Precipitation coupling enabled (pressure -> rain/clouds/radar, wind-advected).");
    }

    #endregion

    #region Cloud Drift

    // Vanilla's precipitation noise barely moves and ignores wind: getPrecipNoise samples
    // `posX/18 + totalDays*18` with no Z time term, so the pattern slides due WEST at a fixed
    // 324 blocks/day (~30x slower than pressure cells advect) and never travels north/south. This became
    // conspicuous once precipitation went multiplicative, since vanilla's noise is now the visible
    // texture rather than one contribution among several.
    //
    // Fix: shift the sample position by the accumulated wind drift before vanilla samples (sampling
    // UPWIND makes the pattern travel DOWNWIND), cancelling vanilla's own westward term at the same time.
    // Applied to posX/posZ only - `conds`/wgenRain (worldgen rainfall baseline) stay untouched, so a
    // region's underlying climate stays put and only the weather riding over it moves.
    /// <summary>Magnitude of vanilla's own hardcoded WESTWARD pattern drift, in blocks per game-day:
    /// getPrecipNoise's first noise axis is <c>posX/18 + totalDays*18</c>, so a feature holds station at
    /// <c>posX = 18u - 324t</c>. Subtracted again in the prefix so our wind is the only thing moving it.</summary>
    private const double VanillaWestwardDriftPerDay = 324.0;

    private static void DriftPrefix(WeatherSystemBase __instance, ref double posX, ref double posZ,
        double totalDays, out object? __state)
    {
        // Postfix needs the TRUE world position, not the drifted sample - Harmony passes whatever this
        // prefix leaves in posX/posZ, so stash the originals here (before any early-out).
        __state = new[] { posX, posZ };

        EnumAppSide side = __instance?.api?.Side ?? EnumAppSide.Server;
        WindField? field = DynamicWindRuntime.FieldFor(side);
        if (field == null) return;

        double totalHours = totalDays * __instance!.api.World.Calendar.HoursPerDay;
        field.CloudDriftAt(totalHours, out double driftX, out double driftZ);

        posX -= driftX + VanillaWestwardDriftPerDay * totalDays;
        posZ -= driftZ;
    }

    #endregion

    #region Coupling Postfix

    private static void Postfix(WeatherSystemBase __instance, ref float __result, double posX, double posZ,
        double totalDays, object? __state)
    {
        // True (un-drifted) position - pressure cells live in world space, not in the noise field's
        // drifting frame. See DriftPrefix.
        if (__state is double[] orig && orig.Length == 2) { posX = orig[0]; posZ = orig[1]; }

        EnumAppSide side = __instance?.api?.Side ?? EnumAppSide.Server;
        WindField? field = DynamicWindRuntime.FieldFor(side);
        if (field == null) return;

        double totalHours = totalDays * (__instance!.api.World.Calendar.HoursPerDay);
        double p = field.CellPressureAt(posX, posZ, totalHours);
        if (p > PressureClamp) p = PressureClamp;
        else if (p < -PressureClamp) p = -PressureClamp;

        // Weight the low side by which front regime(s) influence this position (occluded > cold > warm >
        // stationary, see constants above). Highs have no front regime, so they scale down uniformly.
        double typeMult = 1.0;
        if (p > 0)
        {
            WindField.FrontRegimeBias regime = field.FrontRegimeBiasAt(posX, posZ, totalHours);
            double regimeTotal = regime.Cold + regime.Warm + regime.Occluded + regime.Stationary;
            if (regimeTotal > 1e-6)
                typeMult = (regime.Occluded * OccludedPrecipMult + regime.Cold * ColdPrecipMult
                            + regime.Warm * WarmPrecipMult + regime.Stationary * StationaryPrecipMult) / regimeTotal;
        }

        // Multiply vanilla's cloudness: + pressure (low) scales up past the precip threshold, - (high)
        // scales down. Deliberately proportional - a region vanilla left dry stays dry, since a pressure
        // system modulates an existing pattern rather than inventing one.
        __result *= (float)Math.Exp(StormCouplingStrength * p * typeMult * PrecipLogGain);

        if (__result < 0f) __result = 0f;
    }

    #endregion
}
