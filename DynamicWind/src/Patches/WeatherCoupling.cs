using System;
using System.Reflection;
using DynamicWind.Config;
using DynamicWind.PressureSystem;
using DynamicWind.Utils;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace DynamicWind.Patches;

/// <summary>
/// Couples the pressure field to REAL precipitation at the single chokepoint every consumer funnels
/// through: <see cref="WeatherSystemBase.GetRainCloudness"/>. A postfix shifts the cloudness by the
/// local cell pressure (+ = low → wetter, − = high → clearer; fronts/storms trail a low, a high is
/// fair-weather subsidence), so the same gated value drives the actual falling rain, the climate's
/// Rainfall/RainCloudOverlay (clouds) via the engine's own <c>Event_OnGetClimate</c>, AND the Doppler
/// radar (which reads <c>GetPrecipitation</c>). They can no longer disagree — radar, clouds, and rain
/// are one field. Precipitation moves with the wind because the gating pressure cells advect.
///
/// IMPORTANT: this replaces the old <c>OnGetClimate</c>-event coupling, which only touched NowValues
/// climate and therefore never affected <c>GetPrecipitation</c> (it samples WorldGenValues), so the
/// radar showed rain where none fell. The gate reads <see cref="WindField.CellPressureAt"/> (cells
/// only) — never the climate/thermal path — so it cannot recurse back into GetRainCloudness.
///
/// Applied once (a static guard) even though both the server and client mod systems call it: in
/// singleplayer the patched base method is shared by both weather-system instances, and the postfix
/// picks the side (and thus the field) from the instance's <c>api.Side</c>.
/// </summary>
internal static class WeatherCoupling
{
    private const double PrecipShift = 0.6;     // cloudness units shifted per unit of (clamped) pressure
    private const double PressureClamp = 1.5;
    private const double StormBiasShift = 0.5;  // cloudness units shifted per unit of (clamped) noise term
    private const double StormBiasClamp = 2.0;

    // Front-type weather intensity: an occluded front (cold front has caught the warm one) brings the
    // most severe weather (thunderstorms/thundersnow), a cold front heavy storms, a warm front only light
    // storms, and a stationary front stays relatively calm precipitation-wise — real synoptic behaviour.
    // Applied as a weighted-average multiplier on the low-side noise term below, using
    // WindField.FrontRegimeBiasAt to determine which regime(s) are influencing this position.
    private const double OccludedPrecipMult = 1.4;
    private const double ColdPrecipMult = 1.2;
    private const double WarmPrecipMult = 0.7;
    private const double StationaryPrecipMult = 0.8;

    private static bool patched;

    public static void Apply(Harmony harmony, ILogger logger)
    {
        if (patched) return;
        MethodInfo? target = AccessTools.Method(typeof(WeatherSystemBase), "GetRainCloudness");
        if (target == null)
        {
            logger.Warning("[dynamicwind] WeatherSystemBase.GetRainCloudness not found — precipitation coupling disabled.");
            return;
        }
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(WeatherCoupling), nameof(Postfix)));
        patched = true;
        logger.Notification("[dynamicwind] Precipitation coupling enabled (pressure → rain/clouds/radar).");
    }

    private static void Postfix(WeatherSystemBase __instance, ref float __result, double posX, double posZ, double totalDays)
    {
        EnumAppSide side = __instance?.api?.Side ?? EnumAppSide.Server;
        WindField? field = DynamicWindRuntime.FieldFor(side);
        DynamicWindConfig? config = DynamicWindRuntime.ConfigFor(side);
        if (field == null || config == null || config.StormCouplingStrength <= 0f) return;

        double totalHours = totalDays * (__instance!.api.World.Calendar.HoursPerDay);
        double p = field.CellPressureAt(posX, posZ, totalHours);
        if (p > PressureClamp) p = PressureClamp;
        else if (p < -PressureClamp) p = -PressureClamp;

        // + pressure (low) raises cloudness past the 0.5 precip threshold; − (high) pushes it below.
        __result += (float)(config.StormCouplingStrength * p * PrecipShift);

        // Per-cell weather-noise texture: each active low/high carries its own organic noise map
        // (WindField.CellWeatherNoise, deliberately denser than vanilla's own broad rain-cloudness
        // pattern), gated by the same Gaussian falloff envelope its circulation/pressure already use. A
        // low ADDS its noise on top of the world pattern — patchy heavier/lighter rain bands inside the
        // storm instead of a uniformly "always rainy" disc; a high SUBTRACTS its noise — patchy
        // clearing/holes instead of a perfectly smooth dry disc. Replaces the old smooth directional
        // StormBiasAt push entirely.
        double lowNoise = 0.0, highNoise = 0.0;
        double elapsed = totalHours - field.AnchorHours;
        lock (field.CellsLock)
        {
            foreach (PressureCell c in field.Cells)
            {
                double cx = c.X + c.VelX * elapsed, cz = c.Z + c.VelZ * elapsed;
                double dx = posX - cx, dz = posZ - cz;
                double falloff = Math.Exp(-EllipseGeometry.Norm2(c, dx, dz));
                if (falloff < 1e-4) continue;

                double noise = WindField.CellWeatherNoise(c, posX, posZ, totalHours);
                if (c.IsLow)
                {
                    if (c.StormLikelihood <= 0f) continue; // hasn't developed into a storm yet
                    lowNoise += falloff * Math.Abs(c.Strength) * c.StormLikelihood * noise;
                }
                else
                {
                    highNoise += falloff * Math.Abs(c.Strength) * noise;
                }
            }
        }

        // Weight the low side by which front regime(s) actually influence this position — an occluded
        // low brings severe weather, a cold front heavy storms, a warm front only light ones, and a
        // stationary low stays comparatively calm even while it rains. Highs have no front regime.
        WindField.FrontRegimeBias regime = field.FrontRegimeBiasAt(posX, posZ, totalHours);
        double regimeTotal = regime.Cold + regime.Warm + regime.Occluded + regime.Stationary;
        double typeMult = regimeTotal > 1e-6
            ? (regime.Occluded * OccludedPrecipMult + regime.Cold * ColdPrecipMult
               + regime.Warm * WarmPrecipMult + regime.Stationary * StationaryPrecipMult) / regimeTotal
            : 1.0;

        double noiseTerm = lowNoise * typeMult - highNoise;
        if (noiseTerm > StormBiasClamp) noiseTerm = StormBiasClamp;
        else if (noiseTerm < -StormBiasClamp) noiseTerm = -StormBiasClamp;

        __result += (float)(config.StormCouplingStrength * noiseTerm * StormBiasShift);

        if (__result < 0f) __result = 0f;
    }
}
