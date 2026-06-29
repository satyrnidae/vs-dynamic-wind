using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace DynamicWind;

/// <summary>
/// Couples the pressure field to REAL precipitation at the single chokepoint every consumer funnels
/// through: <see cref="WeatherSystemBase.GetRainCloudness"/>. A postfix shifts the cloudness by the
/// local cell pressure (+ = low → wetter, − = high → clearer), so the same gated value drives the
/// actual falling rain, the climate's Rainfall/RainCloudOverlay (clouds) via the engine's own
/// <c>Event_OnGetClimate</c>, AND the Doppler radar (which reads <c>GetPrecipitation</c>). They can no
/// longer disagree — radar, clouds, and rain are one field. Precipitation moves with the wind because
/// the gating pressure cells advect.
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
        if (__result < 0f) __result = 0f;
    }
}
