using System;
using System.Reflection;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace DynamicWind.Patches;

/// <summary>
/// Couples the pressure field to REAL precipitation at the single chokepoint every consumer funnels
/// through: <see cref="WeatherSystemBase.GetRainCloudness"/>. A postfix shifts the cloudness by the
/// local cell pressure (+ = low → wetter, - = high → clearer; fronts/storms trail a low, a high is
/// fair-weather subsidence), so the same gated value drives the actual falling rain, the climate's
/// Rainfall/RainCloudOverlay (clouds) via the engine's own <c>Event_OnGetClimate</c>, AND the Doppler
/// radar (which reads <c>GetPrecipitation</c>). They can no longer disagree - radar, clouds, and rain
/// are one field. Precipitation moves with the wind because the gating pressure cells advect.
///
/// IMPORTANT: this replaces the old <c>OnGetClimate</c>-event coupling, which only touched NowValues
/// climate and therefore never affected <c>GetPrecipitation</c> (it samples WorldGenValues), so the
/// radar showed rain where none fell. The gate reads <see cref="WindField.CellPressureAt"/> (cells
/// only) - never the climate/thermal path - so it cannot recurse back into GetRainCloudness.
///
/// Applied once (a static guard) even though both the server and client mod systems call it: in
/// singleplayer the patched base method is shared by both weather-system instances, and the postfix
/// picks the side (and thus the field) from the instance's <c>api.Side</c>.
/// </summary>
internal static class WeatherCoupling
{
    // Precipitation is MULTIPLICATIVE on vanilla's own cloudness field, not additive: a low multiplies the
    // existing pattern up, a high divides it down. Vanilla's field already carries organic, broad-scale
    // texture, so modulating it gives patchy bands inside a storm and patchy clearing inside a high for
    // free - no separate per-cell noise map needed (the old CellWeatherNoise term is gone). It is also
    // sign-safe by construction: a high can only ever scale cloudness DOWN, where subtracting a zero-mean
    // noise could raise it over half a high's footprint.
    //
    // exp() of the pressure keeps it strictly positive and makes low/high exact reciprocals: pressure +p
    // multiplies by k, -p divides by the same k.
    private const double PrecipLogGain = 0.55;  // exponent per unit of (clamped) pressure
    private const double PressureClamp = 1.5;

    // NO upper clamp on the result, deliberately. Vanilla clamps where it needs to - Event_OnGetClimate
    // does Clamp(cloudness, 0, 1) into RainCloudOverlay and Clamp(cloudness - 0.5, 0, 1) into Rainfall -
    // but GetPrecipitation is Max(0, cloudness - 0.5) with NO ceiling, so capping cloudness at 1 here
    // would silently cap precipitation at 0.5 and flatten every storm core (and the radar's red band with
    // it). Headroom above 1 is what makes an intense low read as intense.

    // Front-type weather intensity: an occluded front (cold front has caught the warm one) brings the
    // most severe weather (thunderstorms/thundersnow), a cold front heavy storms, a warm front only light
    // storms, and a stationary front stays relatively calm precipitation-wise - real synoptic behaviour.
    // Scales the LOW side of the exponent below, using WindField.FrontRegimeBiasAt to determine which
    // regime(s) are influencing this position (that lookup is itself StormLikelihood-weighted, so an
    // immature low hasn't earned its full intensity yet).
    private const double OccludedPrecipMult = 1.4;
    private const double ColdPrecipMult = 1.2;
    private const double WarmPrecipMult = 0.7;
    private const double StationaryPrecipMult = 0.8;

    // Former DynamicWindConfig tunable, now a fixed constant (see DynamicWindConfig's class remarks).
    private const double StormCouplingStrength = 1.0;

    private static bool patched;

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
        logger.Notification("[dynamicwind] Precipitation coupling enabled (pressure → rain/clouds/radar, wind-advected).");
    }

    // Vanilla's precipitation noise barely moves and never follows the wind: getPrecipNoise samples at
    // `posX/18 + totalDays*18` with NO time term on Z at all, so the whole pattern slides due WEST at a
    // hardcoded 324 blocks/day (13.5/hour) and can never travel north/south - roughly 30x slower than
    // pressure cells advect (AdvectionSpeed 400 blocks/HOUR) and in a fixed direction regardless of the
    // actual wind. That was always odd; it became conspicuous once precipitation went MULTIPLICATIVE on
    // this field, since vanilla's noise is now the visible texture rather than one contribution among
    // several - a near-static stencil reads as a near-static stencil.
    //
    // So shift the sample position by the accumulated wind drift before vanilla ever sees it: sampling
    // UPWIND of where we are makes the pattern travel DOWNWIND. The vanilla westward term is cancelled at
    // the same time so wind is the only thing moving it. Applied to posX/posZ only - `conds` (and hence
    // wgenRain, the worldgen rainfall baseline) is untouched, so a region's underlying climate stays put
    // and only the weather riding over it moves.
    /// <summary>Magnitude of vanilla's own hardcoded WESTWARD pattern drift, in blocks per game-day:
    /// getPrecipNoise's first noise axis is <c>posX/18 + totalDays*18</c>, so a feature holds station at
    /// <c>posX = 18u - 324t</c>. Subtracted again in the prefix so our wind is the only thing moving it.</summary>
    private const double VanillaWestwardDriftPerDay = 324.0;

    private static void DriftPrefix(WeatherSystemBase __instance, ref double posX, ref double posZ,
        double totalDays, out object? __state)
    {
        // The postfix must read pressure at the TRUE world position, not the drifted sample position -
        // Harmony hands it whatever this prefix leaves in posX/posZ, so stash the originals for it. Set
        // before any early-out so the postfix always has them.
        __state = new[] { posX, posZ };

        EnumAppSide side = __instance?.api?.Side ?? EnumAppSide.Server;
        WindField? field = DynamicWindRuntime.FieldFor(side);
        if (field == null) return;

        double totalHours = totalDays * __instance!.api.World.Calendar.HoursPerDay;
        field.CloudDriftAt(totalHours, out double driftX, out double driftZ);

        posX -= driftX + VanillaWestwardDriftPerDay * totalDays;
        posZ -= driftZ;
    }

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

        // Weight the low side by which front regime(s) actually influence this position - an occluded low
        // brings severe weather, a cold front heavy storms, a warm front only light ones, and a stationary
        // low stays comparatively calm even while it rains. Highs have no front regime, so they scale down
        // uniformly.
        double typeMult = 1.0;
        if (p > 0)
        {
            WindField.FrontRegimeBias regime = field.FrontRegimeBiasAt(posX, posZ, totalHours);
            double regimeTotal = regime.Cold + regime.Warm + regime.Occluded + regime.Stationary;
            if (regimeTotal > 1e-6)
                typeMult = (regime.Occluded * OccludedPrecipMult + regime.Cold * ColdPrecipMult
                            + regime.Warm * WarmPrecipMult + regime.Stationary * StationaryPrecipMult) / regimeTotal;
        }

        // Multiply vanilla's cloudness: + pressure (low) scales it up past the precip threshold, - (high)
        // scales it down. Note this is deliberately proportional - a region vanilla left completely dry
        // stays dry, since a pressure system modulates an existing moisture pattern rather than inventing
        // one out of nothing.
        __result *= (float)Math.Exp(StormCouplingStrength * p * typeMult * PrecipLogGain);

        if (__result < 0f) __result = 0f;
    }
}
