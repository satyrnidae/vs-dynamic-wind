using DynamicWind.Config;
using DynamicWind.PressureSystem;
using DynamicWind.WindSystem;
using ProtoBuf;

namespace DynamicWind.Network;

/// <summary>
/// Server→client broadcast of the authoritative wind field: the pressure-cell list plus the base
/// field parameters and the server calendar-time anchor at send. Clients rebuild their local
/// <see cref="WindField"/> from this and interpolate cell positions against their own clock.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class WindFieldState
{
    public PressureCell[] Cells = System.Array.Empty<PressureCell>();
    public double BaseStrength;
    public double MeanderAmplitude;
    public double CyclonicInflowAngle;
    /// <summary>Seasonal N/S jetstream bias factor in [−1, 1] (see <see cref="WindField.SeasonalBiasZ"/>).</summary>
    public double SeasonalBiasZ;
    /// <summary>
    /// Accumulated cloud-texture drift (blocks) at <see cref="TotalHoursAnchor"/>, and the rate it is
    /// currently accumulating at (blocks/game-hour) so the client can extrapolate between snapshots the
    /// same way it extrapolates cell positions — see <see cref="WindField.CloudDriftAt"/>. This has to be
    /// SERVER-ACCUMULATED and synced rather than derived client-side: it is a time integral of the base
    /// flow, and the flow's own seasonal tilt varies over the year, so multiplying an instantaneous
    /// bearing by total elapsed time would shear the whole pattern every time that tilt moved.
    /// Absent on a pre-drift snapshot → protobuf leaves these 0, i.e. no drift. Safe.
    /// </summary>
    public double DriftX;
    public double DriftZ;
    public double DriftVelX;
    public double DriftVelZ;
    /// <summary>Server <c>Calendar.TotalHours</c> when this snapshot was sent.</summary>
    public double TotalHoursAnchor;
}

/// <summary>
/// Client→server request to change the live config (e.g. from the ConfigLib GUI). The server
/// validates the sender's privilege before applying; non-privileged requests are ignored and the
/// client is reverted on the next config broadcast.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class ConfigUpdateRequestPacket
{
    public DynamicWindConfig Config = new();
}

/// <summary>
/// Client→server sail trim INTENT — not an increment. <see cref="Dir"/> is −1 (to port), 0 (stop) or
/// +1 (to starboard); the server holds the latest value per boat and integrates it into the sail
/// angle each tick. Sending intent rather than deltas means a dropped or duplicated packet can't
/// accumulate drift, the wire traffic is one packet per key press/release rather than per tick, and
/// the intent clears itself when the player dismounts or disconnects.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class SailTrimRequestPacket
{
    public long BoatEntityId;
    public sbyte Dir;
}
