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
    [Range(0.0, 500.0)]
    [Description("How strongly wind flows down the pressure gradient (high → low). 0 disables pressure-driven wind.")]
    public float PressureWindStrength { get; set; } = 150f;

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
    [Description("Minimum pressure-cell influence radius, in blocks.")]
    public float CellRadiusMin { get; set; } = 400f;

    [Category("PressureSystems")]
    [Range(50.0, 8000.0)]
    [Description("Maximum pressure-cell influence radius, in blocks.")]
    public float CellRadiusMax { get; set; } = 1200f;

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

    // ── Nested feature blocks ───────────────────────────────────────────────
    public OrographicConfig Orographic { get; set; } = new();
    public WindmillConfig Windmill { get; set; } = new();
    public SailboatConfig Sailboat { get; set; } = new();

    /// <summary>Clamp every value into its valid range. Mutates in place; safe to call repeatedly.</summary>
    public void Validate()
    {
        BaseJetstreamStrength = Clamp(BaseJetstreamStrength, 0f, 3f);
        JetstreamMeanderAmplitude = Clamp(JetstreamMeanderAmplitude, 0f, 1.5f);
        PressureWindStrength = Clamp(PressureWindStrength, 0f, 500f);
        LowSpawnRatePerDay = Clamp(LowSpawnRatePerDay, 0f, 24f);
        HighSpawnRatePerDay = Clamp(HighSpawnRatePerDay, 0f, 24f);
        CellRadiusMin = Clamp(CellRadiusMin, 50f, 8000f);
        CellRadiusMax = Clamp(CellRadiusMax, 50f, 8000f);
        if (CellRadiusMax < CellRadiusMin) CellRadiusMax = CellRadiusMin;
        CyclonicInflowAngleDeg = Clamp(CyclonicInflowAngleDeg, 0f, 60f);
        StormCouplingStrength = Clamp(StormCouplingStrength, 0f, 2f);

        Orographic ??= new OrographicConfig();
        Windmill ??= new WindmillConfig();
        Sailboat ??= new SailboatConfig();
        Orographic.Validate();
        Windmill.Validate();
        Sailboat.Validate();
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
        PressureWindStrength = o.PressureWindStrength;
        LowSpawnRatePerDay = o.LowSpawnRatePerDay;
        HighSpawnRatePerDay = o.HighSpawnRatePerDay;
        CellRadiusMin = o.CellRadiusMin;
        CellRadiusMax = o.CellRadiusMax;
        CyclonicInflowAngleDeg = o.CyclonicInflowAngleDeg;
        StormCouplingStrength = o.StormCouplingStrength;
        CloudsFollowWind = o.CloudsFollowWind;
        Orographic.Enabled = o.Orographic.Enabled;
        Orographic.HeightThreshold = o.Orographic.HeightThreshold;
        Orographic.SlowdownFactor = o.Orographic.SlowdownFactor;
        Orographic.DeflectionStrength = o.Orographic.DeflectionStrength;
        Orographic.WakeFactor = o.Orographic.WakeFactor;
        Windmill.Directionality = o.Windmill.Directionality;
        Windmill.MinEfficiency = o.Windmill.MinEfficiency;
        Sailboat.Enabled = o.Sailboat.Enabled;
        Sailboat.TailwindMaxBoost = o.Sailboat.TailwindMaxBoost;
        Sailboat.HeadwindMaxPenalty = o.Sailboat.HeadwindMaxPenalty;
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class OrographicConfig
{
    [Category("Orographic")]
    [Description("Let terrain slow and deflect wind (mountains block, valleys channel).")]
    public bool Enabled { get; set; } = true;

    [Category("Orographic")]
    [Range(0.0, 255.0)]
    [Description("Terrain height (blocks) above which wind starts to slow.")]
    public float HeightThreshold { get; set; } = 130f;

    [Category("Orographic")]
    [Range(0.0, 1.0)]
    [Description("Maximum fractional wind slowdown over the highest terrain.")]
    public float SlowdownFactor { get; set; } = 0.5f;

    [Category("Orographic")]
    [Range(0.0, 3.0)]
    [Description("How strongly terrain gradients deflect/curve the wind around obstacles.")]
    public float DeflectionStrength { get; set; } = 1.0f;

    [Category("Orographic")]
    [Range(0.0, 1.0)]
    [Description("Wind speed loss in the sheltered lee (downwind) of higher terrain.")]
    public float WakeFactor { get; set; } = 0.5f;

    public void Validate()
    {
        HeightThreshold = DynamicWindConfig.Clamp(HeightThreshold, 0f, 255f);
        SlowdownFactor = DynamicWindConfig.Clamp(SlowdownFactor, 0f, 1f);
        DeflectionStrength = DynamicWindConfig.Clamp(DeflectionStrength, 0f, 3f);
        WakeFactor = DynamicWindConfig.Clamp(WakeFactor, 0f, 1f);
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class WindmillConfig
{
    [Category("Windmill")]
    [Description("Scale windmill power by how well the wind aligns with the rotor axle.")]
    public bool Directionality { get; set; } = true;

    [Category("Windmill")]
    [Range(0.0, 1.0)]
    [Description("Minimum directional efficiency floor so mills don't fully stall when wind is perpendicular to the axle. 0 = can stall completely.")]
    public float MinEfficiency { get; set; } = 0.0f;

    public void Validate()
    {
        MinEfficiency = DynamicWindConfig.Clamp(MinEfficiency, 0f, 1f);
    }
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class SailboatConfig
{
    [Category("Sailboat")]
    [Description("Make sailboat speed depend on wind direction relative to heading.")]
    public bool Enabled { get; set; } = true;

    [Category("Sailboat")]
    [Range(0.0, 2.0)]
    [Description("Fractional speed boost with a pure tailwind (0.5 = +50%).")]
    public float TailwindMaxBoost { get; set; } = 0.5f;

    [Category("Sailboat")]
    [Range(0.0, 1.0)]
    [Description("Fractional speed penalty sailing straight into the wind (0.1 = -10%).")]
    public float HeadwindMaxPenalty { get; set; } = 0.1f;

    public void Validate()
    {
        TailwindMaxBoost = DynamicWindConfig.Clamp(TailwindMaxBoost, 0f, 2f);
        HeadwindMaxPenalty = DynamicWindConfig.Clamp(HeadwindMaxPenalty, 0f, 1f);
    }
}
