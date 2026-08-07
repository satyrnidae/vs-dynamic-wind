using System;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace DynamicWind.WindSystem;

/// <summary>
/// Shared 8-point cardinal naming for a wind vector, so <see cref="Client.Patches.HudClockWindPatch"/>,
/// the weathervane, and the rotorvane all agree on the same bearing math and localization. Names describe
/// where the wind blows FROM (meteorological convention: a "southerly" wind blows toward the north), via
/// the <c>direction-*</c> lang keys.
/// </summary>
internal static class WindCompass
{
    private static readonly string[] Dirs8Keys = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    /// <summary>Localized "-erly" wind name for the given wind X/Z (this repo's convention: +X=East,
    /// +Z=South), or the localized "calm" name if the vector is negligible.</summary>
    public static string GetName(double windX, double windZ)
    {
        if (windX * windX + windZ * windZ < 1e-8) return Lang.Get("dynamicwind:direction-calm");

        // atan2(Z,X): 0=E,90=S,+/-180=W,-90=N (this repo's bearing convention). Compass bearing
        // (0=N,90=E,...) = local + 90.
        double compassDeg = (Math.Atan2(windZ, windX) * GameMath.RAD2DEG + 90 + 360) % 360;
        int idx = (int)Math.Round(compassDeg / 45.0) % 8;
        return Lang.Get("dynamicwind:direction-" + Dirs8Keys[idx]);
    }
}
