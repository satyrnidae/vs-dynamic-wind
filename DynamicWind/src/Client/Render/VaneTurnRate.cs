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

    // Matches WindArrowMapLayer/BlockEntityRotorvane's "100% wind speed" convention (the field's
    // magnitude at calm baseline jetstream flow), so vanes reach full responsiveness at roughly the same
    // strength the map overlay and rotorvane sound would call "100%".
    private const double RefSpeed = 1.0;

    public static float Compute(double windSpeed)
    {
        double frac = windSpeed / RefSpeed;
        if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
        return MinRate + (MaxRate - MinRate) * (float)frac;
    }
}
