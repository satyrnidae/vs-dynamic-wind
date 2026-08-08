using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace DynamicWind.Config;

/// <summary>
/// Server-authoritative configuration. The same object is the on-disk JSON (via
/// <c>LoadModConfig</c>/<c>StoreModConfig</c>) AND the network packet (via protobuf) that the server
/// pushes to clients. Attributes (<see cref="DescriptionAttribute"/>/<see cref="RangeAttribute"/>/
/// <see cref="CategoryAttribute"/>) are read reflectively by the optional ConfigLib integration to
/// build the in-game GUI; they are inert when ConfigLib is absent. ConfigLib's managed-config reflection
/// does NOT use <c>[Category]</c> for visual grouping - it only recognizes the literal tags
/// <c>"logarithmic"</c> (log-scale slider) and <c>"clientside"</c> (bypasses the controlserver privilege
/// check), so it's applied only to properties that need one of those.
///
/// Deliberately limited to feature on/off toggles and settings that affect performance
/// (<see cref="MaxActiveCells"/> bounds the O(n^2) pressure-cell simulation cost). Every other tunable
/// (strengths/rates/angles/thresholds that shape the wind/weather model) is a hardcoded constant at its
/// point of use instead - letting players freely combine dozens of independent numeric knobs made it easy
/// to tune the mod into a broken or degenerate state (e.g. dead-calm air, runaway storms) with no way to
/// validate the combination. All settings are FLAT top-level scalar properties: ConfigLib's managed-config
/// reflection only sees top-level scalars (it does not recurse into nested objects), so flattening makes
/// every knob appear in the GUI.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class DynamicWindConfig
{
    // Jetstream (base flow)
    [Description("description-SeasonalBias")]
    public bool SeasonalBias { get; set; } = true;

    // Pressure systems
    // Range covers the pre-retune default (9) even though the synoptic retune lowered the DEFAULT to 2:
    // Validate() clamps every int to its [Range], so narrowing below the old default would silently
    // rewrite (and re-persist) an existing user's saved value with no migration or log line.
    [Range(0.0, 9.0)]
    [Description("description-MaxActiveCells")]
    public int MaxActiveCells { get; set; } = 2;

    // Humidity
    [Description("description-HumidityEnabled")]
    public bool HumidityEnabled { get; set; } = true;

    [Description("description-HumidityBodyTempCompatEnabled")]
    public bool HumidityBodyTempCompatEnabled { get; set; } = true;

    [ProtoIgnore]
    [Category("clientside")]
    [Description("description-HumidityHudClockLineEnabled")]
    public bool HumidityHudClockLineEnabled { get; set; } = true;

    // Clouds
    [ProtoIgnore]
    [Category("clientside")]
    [Description("description-CloudsFollowWind")]
    public bool CloudsFollowWind { get; set; } = true;

    // Pressure dynamics
    [Description("description-AdvectWithWind")]
    public bool AdvectWithWind { get; set; } = true;

    // Orographic (terrain)
    [Description("description-OrographicEnabled")]
    public bool OrographicEnabled { get; set; } = true;

    // Occlusion (indoor/underground shielding)
    [Description("description-OcclusionEnabled")]
    public bool OcclusionEnabled { get; set; } = true;

    [Description("description-OcclusionRoomsZeroWind")]
    public bool OcclusionRoomsZeroWind { get; set; } = true;

    // Windmill
    [Description("description-WindmillDirectionality")]
    public bool WindmillDirectionality { get; set; } = true;

    // Sailboat
    [Description("description-SailboatEnabled")]
    public bool SailboatEnabled { get; set; } = true;

    // Trimmable sail. Off -> thrust falls back to the older heading-vs-wind alignment curve
    // and the sail never leaves square.
    [Description("description-SailboatSailAngleEnabled")]
    public bool SailboatSailAngleEnabled { get; set; } = true;

    #region Client-side only properties

    [ProtoIgnore]
    [Category("clientside")]
    [Description("description-DopplerRadarMapLayerVisibleByDefault")]
    public bool DopplerRadarMapLayerVisibleByDefault { get; set; } = false;

    [ProtoIgnore]
    [Category("clientside")]
    [Description("description-FrontMapLayerVisibleByDefault")]
    public bool FrontMapLayerVisibleByDefault { get; set; } = false;

    [ProtoIgnore]
    [Category("clientside")]
    [Description("description-WindDirectionMapLayerVisibleByDefault")]
    public bool WindDirectionMapLayerVisibleByDefault { get; set; } = false;

    #endregion

    /// <summary>
    /// Clamp every value into its valid range. Mutates in place; safe to call repeatedly. Bounds are
    /// read reflectively from each property's <see cref="RangeAttribute"/> so the clamp can never drift
    /// from the range ConfigLib's GUI slider advertises.
    /// </summary>
    public void Validate()
    {
        foreach (PropertyInfo prop in typeof(DynamicWindConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            RangeAttribute? range = prop.GetCustomAttribute<RangeAttribute>();
            if (range == null || !prop.CanRead || !prop.CanWrite) continue;

            if (prop.PropertyType == typeof(float))
            {
                float min = Convert.ToSingle(range.Minimum);
                float max = Convert.ToSingle(range.Maximum);
                prop.SetValue(this, GameMath.Clamp((float)prop.GetValue(this)!, min, max));
            }
            else if (prop.PropertyType == typeof(int))
            {
                int min = Convert.ToInt32(range.Minimum);
                int max = Convert.ToInt32(range.Maximum);
                prop.SetValue(this, GameMath.Clamp((int)prop.GetValue(this)!, min, max));
            }
        }
    }

    /// <summary>
    /// Copy all values from another instance into this one (keeps the reference stable, which matters
    /// for the ConfigLib-managed object when the server pushes a synced config down).
    /// </summary>
    public void CopyFrom(DynamicWindConfig o)
    {
        if (o == null) return;
        SeasonalBias = o.SeasonalBias;
        MaxActiveCells = o.MaxActiveCells;
        HumidityEnabled = o.HumidityEnabled;
        HumidityBodyTempCompatEnabled = o.HumidityBodyTempCompatEnabled;
        AdvectWithWind = o.AdvectWithWind;
        OrographicEnabled = o.OrographicEnabled;
        OcclusionEnabled = o.OcclusionEnabled;
        OcclusionRoomsZeroWind = o.OcclusionRoomsZeroWind;
        WindmillDirectionality = o.WindmillDirectionality;
        SailboatEnabled = o.SailboatEnabled;
        SailboatSailAngleEnabled = o.SailboatSailAngleEnabled;
    }
}
