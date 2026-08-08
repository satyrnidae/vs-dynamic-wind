using System;
using DynamicWind.Utils;
using Vintagestory.API.MathTools;

namespace DynamicWind.Client.Render;

/// <summary>
/// Shared by <see cref="WeathervaneRenderer"/> and <see cref="RotorvaneRenderer"/>: a real vane is
/// sluggish in a light breeze (not enough force on the tail to overcome pivot friction) and swings
/// briskly in strong wind, so the yaw-easing rate scales with wind strength instead of using one fixed
/// rate for calm air and a gale alike.
/// </summary>
internal static class VaneTurnRate
{
    private const float MinRate = 0.6f; // rad/s coefficient at near-calm wind
    private const float MaxRate = 3f; // rad/s coefficient at full (100%) wind speed, the old fixed rate

    public static float Compute(double windSpeed)
    {
        double frac = windSpeed / WindReference.Speed;
        if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
        return MinRate + (MaxRate - MinRate) * (float)frac;
    }

    /// <summary>Eases <paramref name="currentYaw"/> toward <paramref name="targetYaw"/> (may be NaN, meaning
    /// "no target yet") at a rate scaled by <paramref name="windSpeed"/>, then updates the shared turn-squeak
    /// sound. Returns the new eased yaw.</summary>
    public static float EaseYaw(float currentYaw, float targetYaw, double windSpeed, float dt, TurnSqueakSound squeak)
    {
        if (!float.IsNaN(targetYaw))
        {
            float delta = GameMath.AngleRadDistance(currentYaw, targetYaw);
            float rate = Compute(windSpeed);
            currentYaw += delta * Math.Min(1f, dt * rate); // smooth swing, faster in stronger wind
        }
        squeak.Update(currentYaw, targetYaw);
        return currentYaw;
    }
}
