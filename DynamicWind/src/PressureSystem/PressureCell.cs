using ProtoBuf;

namespace DynamicWind.PressureSystem;

/// <summary>
/// A moving pressure system. Positive <see cref="Strength"/> = low pressure (cyclonic, draws wind
/// inward and circulates counter-clockwise); negative = high pressure (anticyclonic, pushes wind
/// outward, clockwise). Cells advect roughly west-to-east on the jetstream and expire after a while.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class PressureCell
{
    public double X;
    public double Z;
    /// <summary>+ = low pressure, - = high pressure. Magnitude scales the induced wind speed.</summary>
    public float Strength;
    /// <summary>Influence radius in blocks (gaussian falloff).</summary>
    public float Radius;
    public double VelX;
    public double VelZ;
    public double BornTotalHours;
    public double DiesTotalHours;

    // Appended fields (see WindFieldSimulation/Load): ImplicitFields.AllPublic tags fields by declaration
    // order and this struct is both network-synced and save-persisted, so new fields MUST be added at the
    // end — inserting one earlier would shift the tag numbers of everything after it and corrupt old saves.

    /// <summary>Influence radius along the cell's direction of travel (the along-stream/minor semi-axis),
    /// always &lt;= <see cref="Radius"/> (the cross-stream/major semi-axis). Real fronts are elongated
    /// perpendicular to the flow rather than circular, so this starts close to but below <see cref="Radius"/>
    /// and grows slower as the cell ages, increasing eccentricity over its lifetime.</summary>
    public float RadiusMinor;

    /// <summary>World-space angle (radians) of the major/cross-stream axis. Stored and eased toward the
    /// cell's velocity bearing rather than derived live, so the ellipse rotates smoothly instead of
    /// snapping when velocity changes (see WindFieldSimulation.Advect).</summary>
    public float Orientation;

    /// <summary>Rolling storm-likelihood scalar in [0,1] for this front. Starts at a random value at
    /// spawn and slowly builds over the cell's lifetime (see WindFieldSimulation.Advect) until its cold
    /// front catches another pressure cell and it occludes (see WindFieldSimulation.ApplyOcclusion),
    /// after which it drops hard — the storm winds down once occluded. Gates whether this low's own
    /// weather-noise texture contributes at all (see <c>WeatherCoupling</c>'s per-cell noise-map
    /// mechanic). Cells saved before this field existed deserialize it to the
    /// protobuf default 0; WindFieldSimulation.Load migrates those to 1.0 (see the requirement that
    /// migrated cells default to full storm likelihood).</summary>
    public float StormLikelihood;

    /// <summary>Total-hours timestamp until which this (low) cell renders as an occluded front symbol
    /// instead of separate warm/cold fronts — see FrontComputer.Compute and
    /// WindFieldSimulation.ApplyOcclusion. 0 (the default) or an elapsed timestamp means "not occluded".
    /// Also doubles as a one-shot latch: once non-zero, the cell has already occluded once and won't
    /// re-trigger.</summary>
    public double OccludedUntilTotalHours;

    /// <summary>Intrinsic air-mass character in [-1, 1] this system carries (−1 = cold/polar, +1 =
    /// warm/tropical), independent of whether it's a low or a high — a cold low and a warm high can both
    /// exist, same as a polar low or a subtropical high in reality. Set once at spawn from the cell's
    /// birth latitude + season + random jitter (see WindFieldSimulation.ComputeAirMassTemp) and carried
    /// for the cell's lifetime. Drives the real temperature shift in DynamicWindModCommon.OnGetClimate
    /// via <see cref="DynamicWind.WindSystem.WindField.CellTemperatureAt"/>; also drives which SINGLE front
    /// symbol a moving low draws (warm → poleward/Warm, cold → equatorward/Cold — see
    /// DynamicWind.PressureSystem.FrontComputer) and biases the cell's own advection bearing toward the
    /// pole (warm) or equator (cold) side (see WindFieldSimulation.Advect/AirMassBiasedAngle).</summary>
    public float AirMassTemp;

    /// <summary>Radial reach multiplier for this (low) cell's COLD/equatorward front contour, on top of the
    /// base <see cref="Radius"/>/<see cref="RadiusMinor"/> ellipse — see FrontComputer. Starts at 1 and
    /// grows FASTER than <see cref="WarmFrontReach"/> each hour (WindFieldSimulation.ApplyOcclusion) —
    /// real cold fronts advect/extend faster than warm fronts — so the two sides visibly move and expand
    /// at different rates over the cell's life; the growth rate on each side is itself modulated by how
    /// close this cell's <see cref="AirMassTemp"/> is to whatever pressure cell sits on that side (similar
    /// air masses offer little resistance — sharpen/advance fastest — while starkly different ones slow
    /// the advance). Once this cell's cold-front reach physically reaches another pressure cell on its
    /// equatorward side, the cold front has "caught" that cell and this low occludes (see
    /// WindFieldSimulation.ApplyOcclusion) — a real two-cell event rather than a fixed ratio/age timer.</summary>
    public float ColdFrontReach = 1f;

    /// <summary>Radial reach multiplier for this (low) cell's WARM/poleward front contour. See
    /// <see cref="ColdFrontReach"/>.</summary>
    public float WarmFrontReach = 1f;

    /// <summary>Classification of this low's occlusion, set once when <see cref="OccludedUntilTotalHours"/>
    /// is first triggered (see WindFieldSimulation.ApplyOcclusion): 0 = unset (not yet occluded, or
    /// occluded before this field existed — pre-migration saves fall back to the generic occluded
    /// symbol), 1 = COLD occlusion (the catching cold front's air mass was colder than or equal to the
    /// cell it caught — behaves like a cold front), 2 = WARM occlusion (the catching air was warmer —
    /// behaves like a warm front). Matches the real synoptic distinction between cold-type and warm-type
    /// occlusions. Meaningful only while <see cref="OccludedUntilTotalHours"/> is active.</summary>
    public byte OcclusionKind;

    /// <summary>True for a rare heat-wave/cold-wave high (see WindFieldSimulation.TrySeedHeatColdWave):
    /// a very strong, near-stationary system that traps ground air, pushing local temperature further
    /// from the season's average than an ordinary front/thermal cell. Drives
    /// <see cref="DynamicWind.WindSystem.WindField.HeatColdWaveDeltaAt"/>, kept separate from the normal
    /// <see cref="AirMassTemp"/> blend so its swing isn't diluted by averaging against every other active
    /// cell, and drives a slower velocity-ease rate in WindFieldSimulation.Advect (a lot of inertia — it
    /// lingers over the same area instead of drifting off with the jet like an ordinary cell).</summary>
    public bool IsHeatColdWave;

    /// <summary>Extra flat temperature delta in °C this heat/cold-wave cell adds on top of the normal
    /// front/thermal terms (see DynamicWindModCommon.OnGetClimate), applied with its own falloff via
    /// <see cref="DynamicWind.WindSystem.WindField.HeatColdWaveDeltaAt"/> rather than blended into
    /// <see cref="DynamicWind.WindSystem.WindField.CellTemperatureAt"/>'s weighted average. Positive =
    /// heat wave, negative = cold wave. Meaningful only when <see cref="IsHeatColdWave"/> is true.</summary>
    public float WaveTemperatureDelta;

    // Computed; excluded from protobuf (ImplicitFields.AllPublic would otherwise try to deserialize
    // into this read-only property and throw).
    [ProtoIgnore]
    public bool IsLow => Strength > 0f;
}
