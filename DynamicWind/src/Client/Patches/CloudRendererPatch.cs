using System;
using System.Reflection;
using DynamicWind.Config;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Client.Patches;

/// <summary>
/// Client Harmony patch making clouds drift along the true wind direction. Vanilla
/// <see cref="CloudRenderer.CloudTick"/> eases the cloud-speed vector toward a CONSTANT eastward
/// target (<c>targetCloudSpeedX</c> = random 0..5, plus <c>3×windSpeed</c>, both always positive),
/// so clouds only ever drift roughly +X. Our postfix overwrites both the live speed AND the easing
/// targets with a vector derived straight from the directional wind field, so the easing converges to
/// the real bearing instead of fighting it. The magnitude scales with wind strength (faster clouds in
/// strong flow), and — crucially — when the wind reverses inside a pressure system the clouds reverse
/// with it (the old code projected the eastward-contaminated eased magnitude, which collapsed to ~0 at
/// reversal, stalling the clouds instead of turning them around).
/// Fully guarded: if the method or fields can't be resolved, it logs and no-ops (gameplay wind
/// still works).
/// </summary>
internal static class CloudRendererPatch
{
    // Cloud-speed units per unit of wind-field magnitude. The base jetstream (|w|≈1) gives a moderate
    // drift comparable to vanilla's average; strong flow near a low drifts faster.
    private const float CloudSpeedScale = 3.0f;

    private static ICoreClientAPI capi = null!;
    private static FieldInfo? fCloudSpeedX;
    private static FieldInfo? fCloudSpeedZ;
    private static FieldInfo? fTargetCloudSpeedX;
    private static FieldInfo? fTargetCloudSpeedZ;

    public static void Apply(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        capi = api;
        try
        {
            MethodInfo target = AccessTools.Method(typeof(CloudRenderer), "CloudTick");
            fCloudSpeedX = AccessTools.Field(typeof(CloudRenderer), "cloudSpeedX");
            fCloudSpeedZ = AccessTools.Field(typeof(CloudRenderer), "cloudSpeedZ");
            fTargetCloudSpeedX = AccessTools.Field(typeof(CloudRenderer), "targetCloudSpeedX");
            fTargetCloudSpeedZ = AccessTools.Field(typeof(CloudRenderer), "targetCloudSpeedZ");
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
        if (w.Length() < 1e-6) return;

        // Drive the cloud speed straight from the wind vector (magnitude ∝ wind strength). Set the live
        // speed for this frame AND the easing targets, so vanilla's per-tick easing converges toward our
        // direction rather than constantly pulling the clouds back east. This lets clouds turn and even
        // reverse as a pressure system's circulation overrides the base jetstream.
        float sx = CloudSpeedScale * (float)w.X;
        float sz = CloudSpeedScale * (float)w.Y;
        fCloudSpeedX.SetValue(__instance, sx);
        fCloudSpeedZ.SetValue(__instance, sz);
        fTargetCloudSpeedX?.SetValue(__instance, sx);
        fTargetCloudSpeedZ?.SetValue(__instance, sz);
    }
}
