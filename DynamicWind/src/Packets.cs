using ProtoBuf;

namespace DynamicWind;

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
