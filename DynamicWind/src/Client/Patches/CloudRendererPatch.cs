using System;
using System.Collections.Generic;
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
/// Client Harmony patch making clouds drift along the true wind direction. Two vanilla classes carry an
/// (almost identical, independently duplicated) copy of this logic — <see cref="CloudRenderer"/> (active
/// at cloud-quality setting "simple") and <c>FluffyClouds.CloudRendererMap</c> (active at "volumetric") —
/// selected at runtime by the player's <c>cloudRenderMode</c> setting, so both must be patched or clouds
/// silently keep vanilla drift under one of the two settings. Each eases its cloud-speed vector toward a
/// CONSTANT eastward target (<c>targetCloudSpeedX</c> = random 0..5, plus <c>3×windSpeed</c>, both always
/// positive), so clouds only ever drift roughly +X. Our postfix overwrites both the live speed AND the
/// easing targets with a vector derived straight from the directional wind field, so the easing converges
/// to the real bearing instead of fighting it. The magnitude scales with wind strength (faster clouds in
/// strong flow), and — crucially — when the wind reverses inside a pressure system the clouds reverse
/// with it (the old code projected the eastward-contaminated eased magnitude, which collapsed to ~0 at
/// reversal, stalling the clouds instead of turning them around).
/// Fully guarded per-class: if a type/method/fields can't be resolved (e.g. a future game version renames
/// or removes one of the renderers), that one is skipped with a log and the other still patches.
/// </summary>
internal static class CloudRendererPatch
{
    // Cloud-speed units per unit of wind-field magnitude. The base jetstream (|w|≈1) gives a moderate
    // drift comparable to vanilla's average; strong flow near a low drifts faster.
    private const float CloudSpeedScale = 3.0f;

    private readonly record struct FieldSet(FieldInfo SpeedX, FieldInfo SpeedZ, FieldInfo? TargetX, FieldInfo? TargetZ);

    private static ICoreClientAPI capi = null!;
    private static readonly Dictionary<Type, FieldSet> FieldsByType = new();

    public static void Apply(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        capi = api;
        ApplyTo(harmony, typeof(CloudRenderer), logger);
        ApplyTo(harmony, typeof(FluffyClouds.CloudRendererMap), logger);
    }

    private static void ApplyTo(Harmony harmony, Type rendererType, ILogger logger)
    {
        try
        {
            MethodInfo? target = AccessTools.Method(rendererType, "CloudTick");
            FieldInfo? speedX = AccessTools.Field(rendererType, "cloudSpeedX");
            FieldInfo? speedZ = AccessTools.Field(rendererType, "cloudSpeedZ");
            FieldInfo? targetX = AccessTools.Field(rendererType, "targetCloudSpeedX");
            FieldInfo? targetZ = AccessTools.Field(rendererType, "targetCloudSpeedZ");
            if (target == null || speedX == null || speedZ == null)
            {
                logger.Warning("[dynamicwind] Cloud renderer patch target not found on {0}; clouds keep vanilla drift.", rendererType.FullName);
                return;
            }
            FieldsByType[rendererType] = new FieldSet(speedX, speedZ, targetX, targetZ);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(CloudRendererPatch), nameof(Postfix)));
        }
        catch (Exception e)
        {
            logger.Warning("[dynamicwind] Failed to patch {0}; clouds keep vanilla drift: {1}", rendererType.FullName, e);
        }
    }

    private static void Postfix(object __instance)
    {
        DynamicWindConfig? cfg = DynamicWindRuntime.ClientConfig;
        WindField? field = DynamicWindRuntime.ClientField;
        if (cfg == null || field == null || !cfg.CloudsFollowWind) return;
        if (!FieldsByType.TryGetValue(__instance.GetType(), out FieldSet fields)) return;

        IClientWorldAccessor? world = capi.World;
        var plr = world?.Player?.Entity;
        if (world == null || plr == null) return;

        Vec2d w = field.WindAt(plr.Pos.X, plr.Pos.Z, world.Calendar.TotalHours);
        if (w.Length() < 1e-6) return;

        // Drive the cloud speed straight from the wind vector (magnitude ∝ wind strength). Set the live
        // speed for this frame AND the easing targets, so vanilla's per-tick easing converges toward our
        // direction rather than constantly pulling the clouds back east. This lets clouds turn and even
        // reverse as a pressure system's circulation overrides the base jetstream.
        float sx = CloudSpeedScale * (float)w.X;
        float sz = CloudSpeedScale * (float)w.Y;
        fields.SpeedX.SetValue(__instance, sx);
        fields.SpeedZ.SetValue(__instance, sz);
        fields.TargetX?.SetValue(__instance, sx);
        fields.TargetZ?.SetValue(__instance, sz);
    }
}
