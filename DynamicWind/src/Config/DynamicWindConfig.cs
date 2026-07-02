using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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
    public float LowSpawnRatePerDay { get; set; } = 3.0f;

    [Range(0.0, 24.0)]
    [Description("description-HighSpawnRatePerDay")]
    public float HighSpawnRatePerDay { get; set; } = 2.0f;

    [Category("logarithmic")]
    [Range(50.0, 8000.0)]
    [Description("description-CellRadiusMin")]
    public float CellRadiusMin { get; set; } = 4000f;

    [Category("logarithmic")]
    [Range(50.0, 32000.0)]
    [Description("description-CellRadiusMax")]
    public float CellRadiusMax { get; set; } = 16000f;

    [Range(0.0, 60.0)]
    [Description("description-CyclonicInflowAngleDeg")]
    public float CyclonicInflowAngleDeg { get; set; } = 20f;

    [Range(0.0, 6.0)]
    [Description("description-CirculationStrength")]
    public float CirculationStrength { get; set; } = 2.5f;

    [Range(0.0, 64.0)]
    [Description("description-MaxActiveCells")]
    public int MaxActiveCells { get; set; } = 9;

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

    [Description("description-ThermalSeeding")]
    public bool ThermalSeeding { get; set; } = true;

    [Range(0.0, 1.0)]
    [Description("description-ThermalStaticScale")]
    public float ThermalStaticScale { get; set; } = 0.35f;

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

    [Range(0.0, 1.0)]
    [Description("description-WindmillMinEfficiency")]
    public float WindmillMinEfficiency { get; set; } = 0.0f;

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

    /// <summary>Clamp every value into its valid range. Mutates in place; safe to call repeatedly.</summary>
    public void Validate()
    {
        BaseJetstreamStrength = Clamp(BaseJetstreamStrength, 0f, 3f);
        JetstreamMeanderAmplitude = Clamp(JetstreamMeanderAmplitude, 0f, 1.5f);
        JetstreamMeanderFrequency = Clamp(JetstreamMeanderFrequency, 0.1f, 4f);
        JetstreamNoiseAmplitude = Clamp(JetstreamNoiseAmplitude, 0f, 1f);
        PressureWindStrength = Clamp(PressureWindStrength, 0f, 500f);
        FrontWindStrength = Clamp(FrontWindStrength, 0f, 1f);
        SeasonalBiasMaxAngleDeg = Clamp(SeasonalBiasMaxAngleDeg, 0f, 80f);
        LowSpawnRatePerDay = Clamp(LowSpawnRatePerDay, 0f, 24f);
        HighSpawnRatePerDay = Clamp(HighSpawnRatePerDay, 0f, 24f);
        CellRadiusMin = Clamp(CellRadiusMin, 50f, 16000f);
        CellRadiusMax = Clamp(CellRadiusMax, 50f, 32000f);
        if (CellRadiusMax < CellRadiusMin) CellRadiusMax = CellRadiusMin;
        CyclonicInflowAngleDeg = Clamp(CyclonicInflowAngleDeg, 0f, 60f);
        CirculationStrength = Clamp(CirculationStrength, 0f, 6f);
        if (MaxActiveCells < 0) MaxActiveCells = 0;
        else if (MaxActiveCells > 64) MaxActiveCells = 64;
        StormCouplingStrength = Clamp(StormCouplingStrength, 0f, 2f);
        WeatherPressureCoupling = Clamp(WeatherPressureCoupling, 0f, 2f);
        FrontTemperatureMax = Clamp(FrontTemperatureMax, 0f, 15f);
        ThermalTemperatureMax = Clamp(ThermalTemperatureMax, 0f, 5f);

        AltitudePressureDrop = Clamp(AltitudePressureDrop, 0f, 1f);
        AdvectionSpeed = Clamp(AdvectionSpeed, 0f, 2000f);
        DispersalRate = Clamp(DispersalRate, 0f, 1f);
        BernoulliStrength = Clamp(BernoulliStrength, 0f, 1f);
        ThermalStaticScale = Clamp(ThermalStaticScale, 0f, 1f);

        OrographicHeightThreshold = Clamp(OrographicHeightThreshold, 0f, 255f);
        OrographicSlowdownFactor = Clamp(OrographicSlowdownFactor, 0f, 1f);
        OrographicDeflectionStrength = Clamp(OrographicDeflectionStrength, 0f, 3f);
        OrographicWakeFactor = Clamp(OrographicWakeFactor, 0f, 1f);

        OcclusionLightThreshold = Clamp(OcclusionLightThreshold, 1f, 24f);
        OcclusionSharpness = Clamp(OcclusionSharpness, 1f, 6f);
        OcclusionMinFactor = Clamp(OcclusionMinFactor, 0f, 1f);

        WindmillMinEfficiency = Clamp(WindmillMinEfficiency, 0f, 1f);

        SailboatTailwindMaxBoost = Clamp(SailboatTailwindMaxBoost, 0f, 2f);
        SailboatHeadwindMaxPenalty = Clamp(SailboatHeadwindMaxPenalty, 0f, 1f);

        RotorvaneSpeedMult = Clamp(RotorvaneSpeedMult, 1f, 24f);
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
        AltitudePressureDrop = o.AltitudePressureDrop;
        AdvectWithWind = o.AdvectWithWind;
        AdvectionSpeed = o.AdvectionSpeed;
        DispersalRate = o.DispersalRate;
        BernoulliStrength = o.BernoulliStrength;
        ThermalSeeding = o.ThermalSeeding;
        ThermalStaticScale = o.ThermalStaticScale;
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
