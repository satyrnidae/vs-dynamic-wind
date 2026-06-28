using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind;

/// <summary>
/// Client Harmony patch making clouds drift along the true wind direction. Vanilla
/// <see cref="CloudRenderer.CloudTick"/> accumulates cloud movement almost entirely along +X
/// (strength × fixed axis weights). Our postfix rotates the per-tick cloud-speed vector to align
/// with the wind direction while preserving its magnitude, so clouds follow the real bearing.
/// Fully guarded: if the method or fields can't be resolved, it logs and no-ops (gameplay wind
/// still works).
/// </summary>
internal static class CloudRendererPatch
{
    private static ICoreClientAPI capi = null!;
    private static FieldInfo? fCloudSpeedX;
    private static FieldInfo? fCloudSpeedZ;

    public static void Apply(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        capi = api;
        try
        {
            MethodInfo target = AccessTools.Method(typeof(CloudRenderer), "CloudTick");
            fCloudSpeedX = AccessTools.Field(typeof(CloudRenderer), "cloudSpeedX");
            fCloudSpeedZ = AccessTools.Field(typeof(CloudRenderer), "cloudSpeedZ");
            if (target == null || fCloudSpeedX == null || fCloudSpeedZ == null)
            {
                logger.Warning("[dynamicwind] Cloud renderer patch target not found; clouds keep vanilla drift.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(CloudRendererPatch), nameof(Postfix)));
        }
        catch (Exception e)
        {
            logger.Warning("[dynamicwind] Failed to patch cloud renderer; clouds keep vanilla drift: {0}", e);
        }
    }

    private static void Postfix(object __instance)
    {
        DynamicWindConfig? cfg = DynamicWindRuntime.ClientConfig;
        WindField? field = DynamicWindRuntime.ClientField;
        if (cfg == null || field == null || !cfg.CloudsFollowWind) return;
        if (fCloudSpeedX == null || fCloudSpeedZ == null) return;

        var plr = capi.World?.Player?.Entity;
        if (plr == null) return;

        Vec2d w = field.WindAt(plr.Pos.X, plr.Pos.Z, capi.World.Calendar.TotalHours);
        double len = w.Length();
        if (len < 1e-6) return;

        float cx = (float)fCloudSpeedX.GetValue(__instance)!;
        float cz = (float)fCloudSpeedZ.GetValue(__instance)!;
        double mag = Math.Sqrt(cx * cx + cz * cz);
        if (mag < 1e-6) return;

        fCloudSpeedX.SetValue(__instance, (float)(mag * w.X / len));
        fCloudSpeedZ.SetValue(__instance, (float)(mag * w.Y / len));
    }
}
