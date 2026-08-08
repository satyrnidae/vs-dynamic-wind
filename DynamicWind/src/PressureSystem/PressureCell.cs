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
    #region Core Fields

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

    #endregion

    #region Appended Fields

    // ImplicitFields.AllPublic tags fields by declaration order and this struct is both network-synced and
    // save-persisted, so new fields MUST be added at the end (see WindFieldSimulation/Load) - inserting one
    // earlier would shift the tag numbers of everything after it and corrupt old saves.

    /// <summary>Influence radius along the cell's direction of travel (the along-stream/minor semi-axis),
    /// always &lt;= <see cref="Radius"/> (the cross-stream/major semi-axis). Real fronts are elongated
    /// perpendicular to the flow rather than circular, so this starts close to but below <see cref="Radius"/>
    /// and grows slower as the cell ages, increasing eccentricity over its lifetime.</summary>
    public float RadiusMinor;

    /// <summary>World-space angle (radians) of the major/cross-stream axis. Stored and eased toward the
    /// cell's velocity bearing rather than derived live, so the ellipse rotates smoothly instead of
    /// snapping when velocity changes (see WindFieldSimulation.Advect).</summary>
    public float Orientation;

    /// <summary>Rolling storm-likelihood scalar in [0,1]. Starts random at spawn and slowly builds over
    /// the cell's lifetime (see WindFieldSimulation.Advect) until its cold front catches another cell and
    /// occludes (see WindFieldSimulation.ApplyOcclusion), after which it drops hard - the storm winds down
    /// once occluded. Gates whether this low's own weather-noise texture contributes at all (see
    /// <c>WeatherCoupling</c>'s per-cell noise-map mechanic). Cells saved before this field existed
    /// deserialize to the protobuf default 0; WindFieldSimulation.Load migrates those to 1.0 (full storm
    /// likelihood).</summary>
    public float StormLikelihood;

    /// <summary>Total-hours timestamp until which this (low) cell renders as an occluded front symbol
    /// instead of separate warm/cold fronts - see FrontComputer.Compute and
    /// WindFieldSimulation.ApplyOcclusion. 0 (the default) or an elapsed timestamp means "not occluded".
    /// Also doubles as a one-shot latch: once non-zero, the cell has already occluded once and won't
    /// re-trigger.</summary>
    public double OccludedUntilTotalHours;

    /// <summary>Intrinsic air-mass character in [-1, 1] this system carries (-1 = cold/polar, +1 =
    /// warm/tropical), independent of low/high - a cold low and a warm high can both exist. Set once at
    /// spawn from birth latitude + season + jitter (WindFieldSimulation.ComputeAirMassTemp); drives the
    /// climate temperature shift (WindField.CellTemperatureAt), which single front symbol a moving low
    /// draws, and biases the cell's own advection bearing poleward/equatorward.</summary>
    public float AirMassTemp;

    /// <summary>Radial reach multiplier for this (low) cell's COLD/equatorward front contour, on top of the
    /// base <see cref="Radius"/>/<see cref="RadiusMinor"/> ellipse. Starts at 1, grows FASTER than
    /// <see cref="WarmFrontReach"/> (real cold fronts advect faster - WindFieldSimulation.ApplyOcclusion),
    /// modulated by how close this cell's <see cref="AirMassTemp"/> is to whatever cell sits on that side.
    /// Once it physically reaches another cell on its equatorward side, that cell is "caught" and this low
    /// occludes - see WindFieldSimulation.ApplyOcclusion.</summary>
    public float ColdFrontReach = 1f;

    /// <summary>Radial reach multiplier for this (low) cell's WARM/poleward front contour. See
    /// <see cref="ColdFrontReach"/>.</summary>
    public float WarmFrontReach = 1f;

    /// <summary>Classification of this low's occlusion, set once when <see cref="OccludedUntilTotalHours"/>
    /// is first triggered (see WindFieldSimulation.ApplyOcclusion) - one of <see cref="OcclusionKindUnset"/>/
    /// <see cref="OcclusionKindCold"/>/<see cref="OcclusionKindWarm"/>. Meaningful only while
    /// <see cref="OccludedUntilTotalHours"/> is active. Stored as a raw byte, not an enum, since this field
    /// is protobuf-synced and save-persisted by declaration position (<c>ImplicitFields.AllPublic</c>).</summary>
    public byte OcclusionKind;

    /// <summary>Not yet occluded, or occluded before this field existed (pre-migration saves fall back to
    /// the generic occluded symbol).</summary>
    public const byte OcclusionKindUnset = 0;
    /// <summary>The catching cold front's air mass was colder than or equal to the cell it caught -
    /// behaves like a cold front (matches real synoptic cold-type occlusion).</summary>
    public const byte OcclusionKindCold = 1;
    /// <summary>The catching air was warmer than the cell it caught - behaves like a warm front (matches
    /// real synoptic warm-type occlusion).</summary>
    public const byte OcclusionKindWarm = 2;

    #endregion

    #region Computed

    // Excluded from protobuf (ImplicitFields.AllPublic would otherwise try to deserialize into this
    // read-only property and throw).
    [ProtoIgnore]
    public bool IsLow => Strength > 0f;

    #endregion
}
