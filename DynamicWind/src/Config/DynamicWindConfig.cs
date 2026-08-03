using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ProtoBuf;

namespace DynamicWind.Config;

/// <summary>
/// Server-authoritative configuration. The same object is the on-disk JSON (via
/// <c>LoadModConfig</c>/<c>StoreModConfig</c>) AND the network packet (via protobuf) that the server
/// pushes to clients. Attributes (<see cref="DescriptionAttribute"/>/<see cref="RangeAttribute"/>/
/// <see cref="CategoryAttribute"/>) are read reflectively by the optional ConfigLib integration to
/// build the in-game GUI; they are inert when ConfigLib is absent. ConfigLib's managed-config reflection
/// does NOT use <c>[Category]</c> for visual grouping — it only recognizes the literal tags
/// <c>"logarithmic"</c> (log-scale slider) and <c>"clientside"</c> (bypasses the controlserver privilege
/// check), so it's applied only to properties that need one of those.
///
/// All settings are FLAT top-level scalar properties: ConfigLib's managed-config reflection only sees
/// top-level scalars (it does not recurse into nested objects), so flattening makes every knob appear
/// in the GUI.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class DynamicWindConfig
{
    // ── Jetstream (base flow) ───────────────────────────────────────────────
    [Range(0.0, 3.0)]
    [Description("description-BaseJetstreamStrength")]
    public float BaseJetstreamStrength { get; set; } = 0.7f;

    [Range(0.0, 1.5)]
    [Description("description-JetstreamMeanderAmplitude")]
    public float JetstreamMeanderAmplitude { get; set; } = 0.9f;

    [Category("logarithmic")]
    [Range(0.1, 4.0)]
    [Description("description-JetstreamMeanderFrequency")]
    public float JetstreamMeanderFrequency { get; set; } = 2.0f;

    [Range(0.0, 1.0)]
    [Description("description-JetstreamNoiseAmplitude")]
    public float JetstreamNoiseAmplitude { get; set; } = 0.3f;

    [Category("logarithmic")]
    [Range(0.0, 500.0)]
    [Description("description-PressureWindStrength")]
    public float PressureWindStrength { get; set; } = 150f;

    [Range(0.0, 1.0)]
    [Description("description-FrontWindStrength")]
    public float FrontWindStrength { get; set; } = 0.4f;

    [Description("description-SeasonalBias")]
    public bool SeasonalBias { get; set; } = true;

    [Range(0.0, 80.0)]
    [Description("description-SeasonalBiasMaxAngleDeg")]
    public float SeasonalBiasMaxAngleDeg { get; set; } = 60f;

    // ── Pressure systems ────────────────────────────────────────────────────
    [Range(0.0, 24.0)]
    [Description("description-LowSpawnRatePerDay")]
    public float LowSpawnRatePerDay { get; set; } = 1.5f;

    [Range(0.0, 24.0)]
    [Description("description-HighSpawnRatePerDay")]
    public float HighSpawnRatePerDay { get; set; } = 1.0f;

    [Category("logarithmic")]
    [Range(50.0, 30000.0)]
    [Description("description-CellRadiusMin")]
    public float CellRadiusMin { get; set; } = 15000f;

    [Category("logarithmic")]
    [Range(50.0, 60000.0)]
    [Description("description-CellRadiusMax")]
    public float CellRadiusMax { get; set; } = 30000f;

    [Range(0.0, 60.0)]
    [Description("description-CyclonicInflowAngleDeg")]
    public float CyclonicInflowAngleDeg { get; set; } = 20f;

    [Range(0.0, 6.0)]
    [Description("description-CirculationStrength")]
    public float CirculationStrength { get; set; } = 2.5f;

    // Range covers the pre-retune default (9) even though the synoptic retune lowered the DEFAULT to 2:
    // Validate() clamps every int to its [Range], so narrowing below the old default would silently
    // rewrite (and re-persist) an existing user's saved value with no migration or log line.
    [Range(0.0, 9.0)]
    [Description("description-MaxActiveCells")]
    public int MaxActiveCells { get; set; } = 2;

    // ── Weather coupling ────────────────────────────────────────────────────
    [Range(0.0, 2.0)]
    [Description("description-StormCouplingStrength")]
    public float StormCouplingStrength { get; set; } = 1.0f;

    [Range(0.0, 2.0)]
    [Description("description-WeatherPressureCoupling")]
    public float WeatherPressureCoupling { get; set; } = 0.3f;

    // ── Temperature ─────────────────────────────────────────────────────────
    [Range(0.0, 15.0)]
    [Description("description-FrontTemperatureMax")]
    public float FrontTemperatureMax { get; set; } = 3.0f;

    [Range(0.0, 5.0)]
    [Description("description-ThermalTemperatureMax")]
    public float ThermalTemperatureMax { get; set; } = 1.0f;

    // ── Humidity ────────────────────────────────────────────────────────────
    [Description("description-HumidityEnabled")]
    public bool HumidityEnabled { get; set; } = true;

    [Range(0.0, 1.0)]
    [Description("description-HumidityRainfallBlendWeight")]
    public float HumidityRainfallBlendWeight { get; set; } = 0.7f;

    [Range(0.0, 1.0)]
    [Description("description-HumidityWaterProximityStrength")]
    public float HumidityWaterProximityStrength { get; set; } = 0.25f;

    [Range(0.0, 1.0)]
    [Description("description-HumidityPressureStrength")]
    public float HumidityPressureStrength { get; set; } = 0.15f;

    [Range(0.0, 1.0)]
    [Description("description-HumidityNoiseAmplitude")]
    public float HumidityNoiseAmplitude { get; set; } = 0.1f;

    [Description("description-HumidityBodyTempCompatEnabled")]
    public bool HumidityBodyTempCompatEnabled { get; set; } = true;

    [Range(0.0, 3.0)]
    [Description("description-HumidityDiscomfortMax")]
    public float HumidityDiscomfortMax { get; set; } = 1.5f;

    [Range(0.0, 1.0)]
    [Description("description-HumidityWetnessDryingSuppression")]
    public float HumidityWetnessDryingSuppression { get; set; } = 0.5f;

    [ProtoIgnore]
    [Category("clientside")]
    [Description("description-HumidityHudClockLineEnabled")]
    public bool HumidityHudClockLineEnabled { get; set; } = true;

    // ── Clouds ──────────────────────────────────────────────────────────────
    [ProtoIgnore]
    [Category("clientside")]
    [Description("description-CloudsFollowWind")]
    public bool CloudsFollowWind { get; set; } = true;

    // ── Pressure dynamics ───────────────────────────────────────────────────
    [Range(0.0, 1.0)]
    [Description("description-AltitudePressureDrop")]
    public float AltitudePressureDrop { get; set; } = 0.5f;

    [Description("description-AdvectWithWind")]
    public bool AdvectWithWind { get; set; } = true;

    [Category("logarithmic")]
    [Range(0.0, 2000.0)]
    [Description("description-AdvectionSpeed")]
    public float AdvectionSpeed { get; set; } = 400f;

    [Range(0.0, 1.0)]
    [Description("description-DispersalRate")]
    public float DispersalRate { get; set; } = 0.3f;

    [Range(0.0, 1.0)]
    [Description("description-BernoulliStrength")]
    public float BernoulliStrength { get; set; } = 0.3f;

    // Terrain no longer contributes PRESSURE at all — it is a temperature field, and the coastal wind it
    // drives is now a thermally direct circulation off that field's own gradient (WindField.WindAt).
    [Range(0.0, 3.0)]
    [Description("description-SeaBreezeStrength")]
    public float SeaBreezeStrength { get; set; } = 1.0f;

    // ── Orographic (terrain) ────────────────────────────────────────────────
    [Description("description-OrographicEnabled")]
    public bool OrographicEnabled { get; set; } = true;

    [Range(0.0, 255.0)]
    [Description("description-OrographicHeightThreshold")]
    public float OrographicHeightThreshold { get; set; } = 130f;

    [Range(0.0, 1.0)]
    [Description("description-OrographicSlowdownFactor")]
    public float OrographicSlowdownFactor { get; set; } = 0.5f;

    [Range(0.0, 3.0)]
    [Description("description-OrographicDeflectionStrength")]
    public float OrographicDeflectionStrength { get; set; } = 1.0f;

    [Range(0.0, 1.0)]
    [Description("description-OrographicWakeFactor")]
    public float OrographicWakeFactor { get; set; } = 0.5f;

    // ── Occlusion (indoor/underground shielding) ───────────────────────────
    [Description("description-OcclusionEnabled")]
    public bool OcclusionEnabled { get; set; } = true;

    [Description("description-OcclusionRoomsZeroWind")]
    public bool OcclusionRoomsZeroWind { get; set; } = true;

    [Range(1.0, 24.0)]
    [Description("description-OcclusionLightThreshold")]
    public float OcclusionLightThreshold { get; set; } = 10f;

    [Range(1.0, 6.0)]
    [Description("description-OcclusionSharpness")]
    public float OcclusionSharpness { get; set; } = 3f;

    [Range(0.0, 1.0)]
    [Description("description-OcclusionMinFactor")]
    public float OcclusionMinFactor { get; set; } = 0f;

    // ── Windmill ────────────────────────────────────────────────────────────
    [Description("description-WindmillDirectionality")]
    public bool WindmillDirectionality { get; set; } = true;

    // Efficiency of a rotor sitting exactly PERPENDICULAR to the wind — the bottom of the alignment curve,
    // which scales linearly up to full power head-on (see WindmillDirectionality). NOT a flat floor on the
    // resulting speed.
    [Range(0.0, 1.0)]
    [Description("description-WindmillMinEfficiency")]
    public float WindmillMinEfficiency { get; set; } = 0.5f;

    // ── Sailboat ────────────────────────────────────────────────────────────
    [Description("description-SailboatEnabled")]
    public bool SailboatEnabled { get; set; } = true;

    [Range(0.0, 2.0)]
    [Description("description-SailboatTailwindMaxBoost")]
    public float SailboatTailwindMaxBoost { get; set; } = 0.5f;

    [Range(0.0, 1.0)]
    [Description("description-SailboatHeadwindMaxPenalty")]
    public float SailboatHeadwindMaxPenalty { get; set; } = 0.1f;

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

    [ProtoIgnore]
    [Category("clientside,logarithmic")]
    [Range(8.0, 64.0)]
    [Description("description-RotorvaneSpeedMult")]
    public float RotorvaneSpeedMult { get; set; } = 24f;

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
                prop.SetValue(this, Clamp((float)prop.GetValue(this)!, min, max));
            }
            else if (prop.PropertyType == typeof(int))
            {
                int min = Convert.ToInt32(range.Minimum);
                int max = Convert.ToInt32(range.Maximum);
                int v = (int)prop.GetValue(this)!;
                prop.SetValue(this, v < min ? min : (v > max ? max : v));
            }
        }

        if (CellRadiusMax < CellRadiusMin) CellRadiusMax = CellRadiusMin;
    }

    internal static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

    /// <summary>
    /// Copy all values from another instance into this one (keeps the reference stable, which matters
    /// for the ConfigLib-managed object when the server pushes a synced config down).
    /// </summary>
    public void CopyFrom(DynamicWindConfig o)
    {
        if (o == null) return;
        BaseJetstreamStrength = o.BaseJetstreamStrength;
        JetstreamMeanderAmplitude = o.JetstreamMeanderAmplitude;
        JetstreamMeanderFrequency = o.JetstreamMeanderFrequency;
        JetstreamNoiseAmplitude = o.JetstreamNoiseAmplitude;
        PressureWindStrength = o.PressureWindStrength;
        FrontWindStrength = o.FrontWindStrength;
        SeasonalBias = o.SeasonalBias;
        SeasonalBiasMaxAngleDeg = o.SeasonalBiasMaxAngleDeg;
        LowSpawnRatePerDay = o.LowSpawnRatePerDay;
        HighSpawnRatePerDay = o.HighSpawnRatePerDay;
        CellRadiusMin = o.CellRadiusMin;
        CellRadiusMax = o.CellRadiusMax;
        CyclonicInflowAngleDeg = o.CyclonicInflowAngleDeg;
        CirculationStrength = o.CirculationStrength;
        MaxActiveCells = o.MaxActiveCells;
        StormCouplingStrength = o.StormCouplingStrength;
        WeatherPressureCoupling = o.WeatherPressureCoupling;
        FrontTemperatureMax = o.FrontTemperatureMax;
        ThermalTemperatureMax = o.ThermalTemperatureMax;
        HumidityEnabled = o.HumidityEnabled;
        HumidityRainfallBlendWeight = o.HumidityRainfallBlendWeight;
        HumidityWaterProximityStrength = o.HumidityWaterProximityStrength;
        HumidityPressureStrength = o.HumidityPressureStrength;
        HumidityNoiseAmplitude = o.HumidityNoiseAmplitude;
        HumidityBodyTempCompatEnabled = o.HumidityBodyTempCompatEnabled;
        HumidityDiscomfortMax = o.HumidityDiscomfortMax;
        HumidityWetnessDryingSuppression = o.HumidityWetnessDryingSuppression;
        AltitudePressureDrop = o.AltitudePressureDrop;
        AdvectWithWind = o.AdvectWithWind;
        AdvectionSpeed = o.AdvectionSpeed;
        DispersalRate = o.DispersalRate;
        BernoulliStrength = o.BernoulliStrength;
        SeaBreezeStrength = o.SeaBreezeStrength;
        OrographicEnabled = o.OrographicEnabled;
        OrographicHeightThreshold = o.OrographicHeightThreshold;
        OrographicSlowdownFactor = o.OrographicSlowdownFactor;
        OrographicDeflectionStrength = o.OrographicDeflectionStrength;
        OrographicWakeFactor = o.OrographicWakeFactor;
        OcclusionEnabled = o.OcclusionEnabled;
        OcclusionRoomsZeroWind = o.OcclusionRoomsZeroWind;
        OcclusionLightThreshold = o.OcclusionLightThreshold;
        OcclusionSharpness = o.OcclusionSharpness;
        OcclusionMinFactor = o.OcclusionMinFactor;
        WindmillDirectionality = o.WindmillDirectionality;
        WindmillMinEfficiency = o.WindmillMinEfficiency;
        SailboatEnabled = o.SailboatEnabled;
        SailboatTailwindMaxBoost = o.SailboatTailwindMaxBoost;
        SailboatHeadwindMaxPenalty = o.SailboatHeadwindMaxPenalty;
    }
}
