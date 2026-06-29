using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ProtoBuf;

namespace DynamicWind;

/// <summary>
/// Server-authoritative configuration. The same object is the on-disk JSON (via
/// <c>LoadModConfig</c>/<c>StoreModConfig</c>) AND the network packet (via protobuf) that the server
/// pushes to clients. Attributes (<see cref="DescriptionAttribute"/>/<see cref="RangeAttribute"/>/
/// <see cref="CategoryAttribute"/>) are read reflectively by the optional ConfigLib integration to
/// build the in-game GUI; they are inert when ConfigLib is absent.
///
/// All settings are FLAT top-level scalar properties: ConfigLib's managed-config reflection only sees
/// top-level scalars (it does not recurse into nested objects), so flattening makes every knob appear
/// in the GUI. The <c>[Category]</c> attribute groups them visually.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class DynamicWindConfig
{
    // ── Jetstream (base flow) ───────────────────────────────────────────────
    [Category("Jetstream")]
    [Range(0.0, 3.0)]
    [Description("Strength multiplier of the base west-to-east jetstream flow (1.0 = nominal).")]
    public float BaseJetstreamStrength { get; set; } = 1.0f;

    [Category("Jetstream")]
    [Range(0.0, 1.5)]
    [Description("Maximum meander of the jetstream bearing, in radians. 0 = perfectly straight east.")]
    public float JetstreamMeanderAmplitude { get; set; } = 0.5f;

    [Category("Jetstream")]
    [Range(0.0, 1.0)]
    [Description("Amplitude of the slow jet-streak speed noise (jetstream baseline × 1 ± this). Gives faster/slower jet segments. 0 = constant.")]
    public float JetstreamNoiseAmplitude { get; set; } = 0.3f;

    [Category("Jetstream")]
    [Range(0.0, 500.0)]
    [Description("How strongly wind flows down the pressure gradient (high → low). 0 disables pressure-driven wind.")]
    public float PressureWindStrength { get; set; } = 150f;

    [Category("Jetstream")]
    [Range(0.0, 2.0)]
    [Description("Extra wind speed added by pressure fronts on top of vanilla's wind (additive, so fronts blow even when vanilla wind is calm). 0 = fronts add no wind, wind is purely vanilla's speed in our direction.")]
    public float FrontWindStrength { get; set; } = 0.3f;

    [Category("Jetstream")]
    [Description("Shift the westerly bias north/south with the seasons (poleward in summer → southwesterly, equatorward in winter → northwesterly; mirrored per hemisphere).")]
    public bool SeasonalBias { get; set; } = true;

    [Category("Jetstream")]
    [Range(0.0, 80.0)]
    [Description("Maximum north/south tilt of the westerly at peak summer/winter, in degrees (45 ≈ pure NW/SW at the extremes).")]
    public float SeasonalBiasMaxAngleDeg { get; set; } = 45f;

    // ── Pressure systems ────────────────────────────────────────────────────
    [Category("PressureSystems")]
    [Range(0.0, 24.0)]
    [Description("Average number of low-pressure systems (storms) spawned per game day.")]
    public float LowSpawnRatePerDay { get; set; } = 3.0f;

    [Category("PressureSystems")]
    [Range(0.0, 24.0)]
    [Description("Average number of high-pressure systems (clear/calm) spawned per game day.")]
    public float HighSpawnRatePerDay { get; set; } = 2.0f;

    [Category("PressureSystems")]
    [Range(50.0, 8000.0)]
    [Description("Minimum pressure-cell influence radius, in blocks (safety clamp; cell size is derived from the loaded-chunk extent).")]
    public float CellRadiusMin { get; set; } = 50f;

    [Category("PressureSystems")]
    [Range(50.0, 8000.0)]
    [Description("Maximum pressure-cell influence radius, in blocks (safety clamp; cell size is derived from the loaded-chunk extent).")]
    public float CellRadiusMax { get; set; } = 8000f;

    [Category("PressureSystems")]
    [Range(0.0, 60.0)]
    [Description("Cyclonic inflow/outflow tilt of circulation around a pressure cell, in degrees.")]
    public float CyclonicInflowAngleDeg { get; set; } = 20f;

    // ── Weather coupling ────────────────────────────────────────────────────
    [Category("Weather")]
    [Range(0.0, 2.0)]
    [Description("How strongly low pressure biases weather toward storms (and raises wind strength). 0 disables coupling.")]
    public float StormCouplingStrength { get; set; } = 1.0f;

    // ── Clouds ──────────────────────────────────────────────────────────────
    [Category("Clouds")]
    [Description("Make clouds drift along the true wind direction (requires patching the cloud renderer).")]
    public bool CloudsFollowWind { get; set; } = true;

    // ── Pressure dynamics ───────────────────────────────────────────────────
    [Category("PressureDynamics")]
    [Range(0.0, 1.0)]
    [Description("Fractional pressure drop reached at the build ceiling (0.5 = −50%). Pressure falls with altitude toward 'low'; no effect at/below sea level.")]
    public float AltitudePressureDrop { get; set; } = 0.5f;

    [Category("PressureDynamics")]
    [Description("Advect pressure cells along the local wind (so the pressure differential moves with the wind) instead of a fixed jetstream velocity.")]
    public bool AdvectWithWind { get; set; } = true;

    [Category("PressureDynamics")]
    [Range(0.0, 2000.0)]
    [Description("Cell drift speed in blocks per game-hour per unit of wind strength. Higher = pressure systems travel faster.")]
    public float AdvectionSpeed { get; set; } = 400f;

    [Category("PressureDynamics")]
    [Range(0.0, 1.0)]
    [Description("How fast cells spread out (radius grows) and fade (strength decays) over their life. 0 = no dispersal.")]
    public float DispersalRate { get; set; } = 0.3f;

    [Category("PressureDynamics")]
    [Range(0.0, 1.0)]
    [Description("Bernoulli coupling: faster-moving air lowers local pressure, stagnant air raises it. 0 disables speed→pressure feedback.")]
    public float BernoulliStrength { get; set; } = 0.3f;

    [Category("PressureDynamics")]
    [Description("Spawn mobile, dispersing pressure cells from terrain thermal hotspots (hot dry land → low, deep water → high) so terrain influence drifts with the wind.")]
    public bool ThermalSeeding { get; set; } = true;

    [Category("PressureDynamics")]
    [Range(0.0, 1.0)]
    [Description("How much of the raw terrain thermal field stays as a static, terrain-pinned baseline (the rest is expressed as mobile seeded cells). 1 = fully static (old behaviour), 0 = fully mobile.")]
    public float ThermalStaticScale { get; set; } = 0.35f;

    // ── Orographic (terrain) ────────────────────────────────────────────────
    [Category("Orographic")]
    [Description("Let terrain slow and deflect wind (mountains block, valleys channel).")]
    public bool OrographicEnabled { get; set; } = true;

    [Category("Orographic")]
    [Range(0.0, 255.0)]
    [Description("Terrain height (blocks) above which wind starts to slow.")]
    public float OrographicHeightThreshold { get; set; } = 130f;

    [Category("Orographic")]
    [Range(0.0, 1.0)]
    [Description("Maximum fractional wind slowdown over the highest terrain.")]
    public float OrographicSlowdownFactor { get; set; } = 0.5f;

    [Category("Orographic")]
    [Range(0.0, 3.0)]
    [Description("How strongly terrain gradients deflect/curve the wind around obstacles.")]
    public float OrographicDeflectionStrength { get; set; } = 1.0f;

    [Category("Orographic")]
    [Range(0.0, 1.0)]
    [Description("Wind speed loss in the sheltered lee (downwind) of higher terrain.")]
    public float OrographicWakeFactor { get; set; } = 0.5f;

    // ── Windmill ────────────────────────────────────────────────────────────
    [Category("Windmill")]
    [Description("Scale windmill power by how well the wind aligns with the rotor axle.")]
    public bool WindmillDirectionality { get; set; } = true;

    [Category("Windmill")]
    [Range(0.0, 1.0)]
    [Description("Minimum directional efficiency floor so mills don't fully stall when wind is perpendicular to the axle. 0 = can stall completely.")]
    public float WindmillMinEfficiency { get; set; } = 0.0f;

    // ── Sailboat ────────────────────────────────────────────────────────────
    [Category("Sailboat")]
    [Description("Make sailboat speed depend on wind direction relative to heading.")]
    public bool SailboatEnabled { get; set; } = true;

    [Category("Sailboat")]
    [Range(0.0, 2.0)]
    [Description("Fractional speed boost with a pure tailwind (0.5 = +50%).")]
    public float SailboatTailwindMaxBoost { get; set; } = 0.5f;

    [Category("Sailboat")]
    [Range(0.0, 1.0)]
    [Description("Fractional speed penalty sailing straight into the wind (0.1 = -10%).")]
    public float SailboatHeadwindMaxPenalty { get; set; } = 0.1f;

    /// <summary>Clamp every value into its valid range. Mutates in place; safe to call repeatedly.</summary>
    public void Validate()
    {
        BaseJetstreamStrength = Clamp(BaseJetstreamStrength, 0f, 3f);
        JetstreamMeanderAmplitude = Clamp(JetstreamMeanderAmplitude, 0f, 1.5f);
        JetstreamNoiseAmplitude = Clamp(JetstreamNoiseAmplitude, 0f, 1f);
        PressureWindStrength = Clamp(PressureWindStrength, 0f, 500f);
        FrontWindStrength = Clamp(FrontWindStrength, 0f, 2f);
        SeasonalBiasMaxAngleDeg = Clamp(SeasonalBiasMaxAngleDeg, 0f, 80f);
        LowSpawnRatePerDay = Clamp(LowSpawnRatePerDay, 0f, 24f);
        HighSpawnRatePerDay = Clamp(HighSpawnRatePerDay, 0f, 24f);
        CellRadiusMin = Clamp(CellRadiusMin, 50f, 8000f);
        CellRadiusMax = Clamp(CellRadiusMax, 50f, 8000f);
        if (CellRadiusMax < CellRadiusMin) CellRadiusMax = CellRadiusMin;
        CyclonicInflowAngleDeg = Clamp(CyclonicInflowAngleDeg, 0f, 60f);
        StormCouplingStrength = Clamp(StormCouplingStrength, 0f, 2f);

        AltitudePressureDrop = Clamp(AltitudePressureDrop, 0f, 1f);
        AdvectionSpeed = Clamp(AdvectionSpeed, 0f, 2000f);
        DispersalRate = Clamp(DispersalRate, 0f, 1f);
        BernoulliStrength = Clamp(BernoulliStrength, 0f, 1f);
        ThermalStaticScale = Clamp(ThermalStaticScale, 0f, 1f);

        OrographicHeightThreshold = Clamp(OrographicHeightThreshold, 0f, 255f);
        OrographicSlowdownFactor = Clamp(OrographicSlowdownFactor, 0f, 1f);
        OrographicDeflectionStrength = Clamp(OrographicDeflectionStrength, 0f, 3f);
        OrographicWakeFactor = Clamp(OrographicWakeFactor, 0f, 1f);

        WindmillMinEfficiency = Clamp(WindmillMinEfficiency, 0f, 1f);

        SailboatTailwindMaxBoost = Clamp(SailboatTailwindMaxBoost, 0f, 2f);
        SailboatHeadwindMaxPenalty = Clamp(SailboatHeadwindMaxPenalty, 0f, 1f);
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
        StormCouplingStrength = o.StormCouplingStrength;
        CloudsFollowWind = o.CloudsFollowWind;
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
        WindmillDirectionality = o.WindmillDirectionality;
        WindmillMinEfficiency = o.WindmillMinEfficiency;
        SailboatEnabled = o.SailboatEnabled;
        SailboatTailwindMaxBoost = o.SailboatTailwindMaxBoost;
        SailboatHeadwindMaxPenalty = o.SailboatHeadwindMaxPenalty;
    }
}
