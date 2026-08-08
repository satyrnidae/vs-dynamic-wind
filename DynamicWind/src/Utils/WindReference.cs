namespace DynamicWind.Utils;

/// <summary>The wind speed magnitude treated as "100%" throughout the mod (vane turn rate, rotorvane
/// spin/sound, wind-arrow map layer scaling, sailboat wind boost) - the calm-baseline jetstream field
/// magnitude, not a hard cap (front excess and storms both read well above this).</summary>
internal static class WindReference
{
    public const double Speed = 1.0;
}
