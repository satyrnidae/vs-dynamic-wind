using System;
using System.Reflection;
using DynamicWind.Config;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace DynamicWind.Compat;

/// <summary>
/// Optional compat with the "Immersive Body Temperature" mod (modid <see cref="ModId"/>, not a compile-time
/// or modinfo dependency — resolved entirely via <see cref="AccessTools"/>, isolated here so a missing DLL
/// can never cause a type-load failure, same convention as <see cref="Client.DynamicWindConfigLib"/>).
///
/// Confirmed by decompiling the mod directly (not assumed): its <c>Modifiers.HumidityModifier</c> is
/// currently an inert stub (always contributes 0 °C), and its <c>Modifiers.WetnessModifier</c> reads live
/// <c>ClimateCondition.Rainfall</c> for the wetness-GAIN term only — the wetness-drying term is a flat,
/// climate-independent server config value (<c>ModConfigServer.Instance.WetnessDrySpeed</c>). So neither
/// "humidity reduces cooling effectiveness" nor "stay damp longer" already happens once Dynamic Wind feeds
/// live Rainfall (see <see cref="DynamicWindModCommon.OnGetClimate"/>) — both need new patches here.
///
/// Server-only: <c>EntityBehaviorBodyTemperature</c>'s modifiers tick on the server (entity/watched-attribute
/// state is server-authoritative), so this reads <see cref="DynamicWindRuntime.ServerField"/>/
/// <see cref="DynamicWindRuntime.ServerConfig"/> directly rather than side-dispatching like
/// <see cref="Patches.WeatherCoupling"/> does.
///
/// Entirely gated by <see cref="Config.DynamicWindConfig.HumidityBodyTempCompatEnabled"/> (checked by the
/// caller before <see cref="Apply"/> is even invoked) — this reaches into another mod's private tick logic
/// and its exact internals could change on any update of that mod, so it must be trivial to turn off.
/// </summary>
internal static class ImmersiveBodyTemperatureCompat
{
    public const string ModId = "warmweathereffects";

    private const string ModifiersNamespace = "VS_ImmersiveBodyTemperature.Modifiers";

    private static bool patched;

    // Resolved once in Apply, reused by both postfixes.
    private static PropertyInfo? entityGetter;       // TemperatureModifier.Entity (protected get)
    private static MethodInfo? temperatureSetter;     // TemperatureModifier.Temperature (protected set)
    private static PropertyInfo? wetnessProp;          // WetnessModifier.Wetness (private get/set)
    private static PropertyInfo? lastWetnessUpdateProp; // WetnessModifier.LastWetnessUpdateTotalHours (private get/set)
    private static PropertyInfo? wetnessDrySpeedProp;  // ModConfigServer.Instance.WetnessDrySpeed (public)
    private static PropertyInfo? modConfigServerInstanceProp; // ModConfigServer.Instance (public static)

    public static void Apply(Harmony harmony, ILogger logger)
    {
        if (patched) return;

        Type? temperatureModifierType = AccessTools.TypeByName(ModifiersNamespace + ".TemperatureModifier");
        Type? humidityModifierType = AccessTools.TypeByName(ModifiersNamespace + ".HumidityModifier");
        Type? wetnessModifierType = AccessTools.TypeByName(ModifiersNamespace + ".WetnessModifier");
        Type? modConfigServerType = AccessTools.TypeByName("VS_ImmersiveBodyTemperature.ModConfigServer");

        entityGetter = temperatureModifierType == null ? null : AccessTools.Property(temperatureModifierType, "Entity");
        temperatureSetter = temperatureModifierType == null ? null : AccessTools.PropertySetter(temperatureModifierType, "Temperature");
        MethodInfo? humidityCalc = humidityModifierType == null ? null : AccessTools.Method(humidityModifierType, "InternalCalculate");
        MethodInfo? wetnessCalc = wetnessModifierType == null ? null : AccessTools.Method(wetnessModifierType, "InternalCalculate");
        wetnessProp = wetnessModifierType == null ? null : AccessTools.Property(wetnessModifierType, "Wetness");
        lastWetnessUpdateProp = wetnessModifierType == null ? null : AccessTools.Property(wetnessModifierType, "LastWetnessUpdateTotalHours");
        modConfigServerInstanceProp = modConfigServerType == null ? null : AccessTools.Property(modConfigServerType, "Instance");
        wetnessDrySpeedProp = modConfigServerType == null ? null : AccessTools.Property(modConfigServerType, "WetnessDrySpeed");

        if (entityGetter == null || temperatureSetter == null || humidityCalc == null || wetnessCalc == null
            || wetnessProp == null || lastWetnessUpdateProp == null || modConfigServerInstanceProp == null || wetnessDrySpeedProp == null)
        {
            logger.Warning("[dynamicwind] Immersive Body Temperature compat targets not found; skipping (mod may have updated internals).");
            return;
        }

        harmony.Patch(humidityCalc, postfix: new HarmonyMethod(typeof(ImmersiveBodyTemperatureCompat), nameof(HumidityPostfix)));
        harmony.Patch(wetnessCalc,
            prefix: new HarmonyMethod(typeof(ImmersiveBodyTemperatureCompat), nameof(WetnessPrefix)),
            postfix: new HarmonyMethod(typeof(ImmersiveBodyTemperatureCompat), nameof(WetnessPostfix)));
        patched = true;
        logger.Notification("[dynamicwind] Immersive Body Temperature compat enabled (humidity → cooling effectiveness + wetness drying).");
    }

    /// <summary>Below this temperature, humidity doesn't impair evaporative cooling — the effect only makes
    /// sense when the body is trying to shed heat.</summary>
    private const double WarmThresholdC = 20.0;
    private const double WarmFadeRangeC = 8.0; // fades in over this many °C above the threshold

    private static void HumidityPostfix(object __instance)
    {
        WindField? field = DynamicWindRuntime.ServerField;
        DynamicWindConfig? config = DynamicWindRuntime.ServerConfig;
        if (field == null || config == null || config.HumidityDiscomfortMax <= 0f) return;

        var entity = entityGetter!.GetValue(__instance) as Entity;
        if (entity?.World == null) return;

        double totalHours = entity.World.Calendar.TotalHours;
        double humidity = field.HumidityAt(entity.Pos.X, entity.Pos.Z, totalHours);

        double warmth = entity.World.BlockAccessor
            .GetClimateAt(entity.Pos.AsBlockPos, EnumGetClimateMode.NowValues, 0.0)?.Temperature ?? 20f;
        double warmFactor = (warmth - WarmThresholdC) / WarmFadeRangeC;
        if (warmFactor < 0) warmFactor = 0; else if (warmFactor > 1) warmFactor = 1;
        if (warmFactor <= 0) return;

        float discomfort = (float)(config.HumidityDiscomfortMax * humidity * warmFactor);
        temperatureSetter!.Invoke(__instance, new object[] { discomfort });
    }

    private static void WetnessPrefix(object __instance, out (Entity? entity, double lastUpdateHours, float wetnessBefore) __state)
    {
        var entity = entityGetter!.GetValue(__instance) as Entity;
        double lastUpdate = (double)lastWetnessUpdateProp!.GetValue(__instance)!;
        float wetnessBefore = (float)wetnessProp!.GetValue(__instance)!;
        __state = (entity, lastUpdate, wetnessBefore);
    }

    private static void WetnessPostfix(object __instance, (Entity? entity, double lastUpdateHours, float wetnessBefore) __state)
    {
        WindField? field = DynamicWindRuntime.ServerField;
        DynamicWindConfig? config = DynamicWindRuntime.ServerConfig;
        if (field == null || config == null || config.HumidityWetnessDryingSuppression <= 0f) return;

        Entity? entity = __state.entity;
        if (entity?.World == null) return;

        double nowHours = entity.World.Calendar.TotalHours;
        double hoursElapsed = nowHours - __state.lastUpdateHours;
        if (hoursElapsed <= 0) return;

        double humidity = field.HumidityAt(entity.Pos.X, entity.Pos.Z, nowHours);
        if (humidity <= 0) return;

        object modConfigServer = modConfigServerInstanceProp!.GetValue(null)!;
        double drySpeed = (float)wetnessDrySpeedProp!.GetValue(modConfigServer)!;

        double restore = config.HumidityWetnessDryingSuppression * humidity * hoursElapsed * drySpeed;
        if (restore <= 0) return;

        float wetnessAfter = (float)wetnessProp!.GetValue(__instance)!;
        // Never restore more than was just dried this tick, and re-clamp to the valid [0, 1] range.
        float newWetness = (float)Math.Min(__state.wetnessBefore, wetnessAfter + restore);
        if (newWetness < 0f) newWetness = 0f; else if (newWetness > 1f) newWetness = 1f;
        wetnessProp!.SetValue(__instance, newWetness);
    }
}
