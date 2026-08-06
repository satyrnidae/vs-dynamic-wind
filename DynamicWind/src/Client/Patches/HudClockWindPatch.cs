using System;
using System.Reflection;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace DynamicWind.Client.Patches;

/// <summary>
/// Optional runtime patch for the locally-installed third-party "HUD Clock" mod (modid
/// <see cref="HudClockModId"/>). Its own wind readout calls
/// <c>WeatherSystemBase.WeatherDataSlowAccess.GetWindSpeed(pos)</c> directly - vanilla's raw scalar
/// noise source - instead of <see cref="IBlockAccessor.GetWindSpeedAt(BlockPos)"/>, the API
/// <see cref="DynamicWindModCommon.OnGetWindSpeed"/> actually rewrites. So it silently disagreed with
/// this mod's true wind field and had no concept of direction at all.
///
/// Postfixes HUD Clock's <c>MainHudViewModel.Tick()</c> to overwrite its private <c>_windSpeed</c>
/// field with the correct directional magnitude, and its <c>WindText</c> getter to append an 8-point
/// cardinal - after whatever text its own Percentage/Beaufort/Hidden setting already produced, so that
/// setting keeps working unmodified (Hidden mode still yields null, nothing appended).
///
/// Resolved entirely via <see cref="AccessTools"/> (HUD Clock is not a compile-time dependency; it's
/// not even in <c>modinfo.json</c>) so a HUD Clock update that renames/removes these members just warns
/// and no-ops instead of breaking anything.
/// </summary>
internal static class HudClockWindPatch
{
    public const string HudClockModId = "hudclockpatch";

    private static ICoreClientAPI capi = null!;
    private static FieldInfo windSpeedField = null!;
    private static FieldInfo posProviderField = null!;
    private static string? lastCardinal;

    public static void Apply(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        try
        {
            Type? vmType = AccessTools.TypeByName("HudClock.Presentation.MainHud.MainHudViewModel");
            MethodInfo? tick = vmType == null ? null : AccessTools.Method(vmType, "Tick");
            MethodInfo? windTextGetter = vmType == null ? null : AccessTools.PropertyGetter(vmType, "WindText");
            FieldInfo? windSpeed = vmType == null ? null : AccessTools.Field(vmType, "_windSpeed");
            FieldInfo? posProvider = vmType == null ? null : AccessTools.Field(vmType, "_playerPosition");

            if (vmType == null || tick == null || windTextGetter == null || windSpeed == null || posProvider == null)
            {
                logger.Warning("[dynamicwind] HUD Clock patch target not found; its wind display keeps vanilla values.");
                return;
            }

            capi = api;
            windSpeedField = windSpeed;
            posProviderField = posProvider;

            harmony.Patch(tick, postfix: new HarmonyMethod(typeof(HudClockWindPatch), nameof(TickPostfix)));
            harmony.Patch(windTextGetter, postfix: new HarmonyMethod(typeof(HudClockWindPatch), nameof(WindTextPostfix)));

            logger.Notification("[dynamicwind] HUD Clock wind display patched to use the directional wind field.");
        }
        catch (Exception e)
        {
            logger.Warning("[dynamicwind] Failed to patch HUD Clock wind display: {0}", e);
        }
    }

    private static void TickPostfix(object __instance)
    {
        BlockPos? pos = (posProviderField.GetValue(__instance) as Delegate)?.DynamicInvoke() as BlockPos;
        if (pos == null) return;

        // HUD Clock's provider gives the player's foot position; occlusion (room/skylight checks) should
        // instead reflect what the player's head actually has sky access to; a player standing in a
        // shallow pit with their head clear of it shouldn't read as occluded, and vice versa.
        Entity? plr = capi.World.Player?.Entity;
        Vec3d queryPos = pos.ToVec3d();
        if (plr != null) queryPos.Y = plr.Pos.Y + plr.LocalEyePos.Y;

        Vec3d wind = capi.World.BlockAccessor.GetWindSpeedAt(queryPos);
        double mag = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
        windSpeedField.SetValue(__instance, mag);

        if (mag < 1e-6)
        {
            lastCardinal = null;
            return;
        }

        // Parens are this HUD's own display style; WindCompass just gives the plain localized name.
        lastCardinal = "(" + WindCompass.GetName(wind.X, wind.Z) + ")";
    }

    private static void WindTextPostfix(ref string __result)
    {
        if (__result == null || lastCardinal == null) return;
        __result = __result + " " + lastCardinal;
    }
}
