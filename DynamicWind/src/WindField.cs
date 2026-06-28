using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace DynamicWind;

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

    // Computed; excluded from protobuf (ImplicitFields.AllPublic would otherwise try to deserialize
    // into this read-only property and throw).
    [ProtoIgnore]
    public bool IsLow => Strength > 0f;
}

/// <summary>
/// Deterministic wind field shared by server and client. Given the same parameters and cell list
/// (kept in sync via <see cref="WindFieldState"/>), <see cref="WindAt"/> returns identical results
/// on both sides, so clients never need per-block wind streaming.
///
/// Convention: +X = East, +Z = South. The returned vector points in the wind's travel direction;
/// its magnitude is the strength multiplier applied on top of the vanilla scalar wind speed.
/// The base jetstream blows +X (west-to-east) with a slow large-scale meander.
/// </summary>
public class WindField
{
    public double BaseStrength = 1.0;
    /// <summary>Maximum meander of the base jetstream angle, in radians.</summary>
    public double MeanderAmplitude = 0.5;
    /// <summary>Inflow/outflow tilt of cyclonic circulation, in radians (~20°).</summary>
    public double CyclonicInflowAngle = 0.35;

    /// <summary>
    /// Calendar time (total hours) at which each cell's <see cref="PressureCell.X"/>/<c>Z</c> was the
    /// true position. Effective positions are advected by velocity from this anchor, so a single
    /// snapshot keeps server and client identical between updates.
    /// </summary>
    public double AnchorHours;

    public readonly List<PressureCell> Cells = new();

    /// <summary>
    /// Optional terrain-driven thermal pressure contribution at (x, z, totalHours). Set per side
    /// (server uses world data; client samples its loaded world). + = low (warm land), − = high
    /// (cool water). Added on top of the moving cell pressure in <see cref="PressureAt"/>.
    /// </summary>
    public Func<double, double, double, double>? ThermalPressureFunc;

    /// <summary>
    /// Slow, deterministic large-scale meander in [-1, 1]. A handful of incommensurate sines gives an
    /// organic wander that is identical on server and client without needing a shared noise seed.
    /// </summary>
    public static double Meander(double x, double z, double totalHours)
    {
        double a = Math.Sin(x * 0.0005 + totalHours * 0.010);
        double b = Math.Sin(z * 0.0005 - totalHours * 0.013);
        double c = Math.Sin((x + z) * 0.0003 + totalHours * 0.007);
        return (a + b + c) / 3.0;
    }

    /// <summary>
    /// Wind vector at a world position. X/Z components are East/South; the magnitude is the strength
    /// multiplier (≈1.0 in calm jetstream flow, higher near strong lows).
    /// </summary>
    public Vec2d WindAt(double x, double z, double totalHours)
    {
        // Base jetstream: due-East flow with a slow meandering bearing.
        double meander = MeanderAmplitude * Meander(x, z, totalHours);
        double wx = BaseStrength * Math.Cos(meander);
        double wz = BaseStrength * Math.Sin(meander);

        double cosIn = Math.Cos(CyclonicInflowAngle);
        double sinIn = Math.Sin(CyclonicInflowAngle);
        double elapsed = totalHours - AnchorHours;

        for (int i = 0; i < Cells.Count; i++)
        {
            PressureCell cell = Cells[i];
            double cellX = cell.X + cell.VelX * elapsed;
            double cellZ = cell.Z + cell.VelZ * elapsed;
            double dx = x - cellX;
            double dz = z - cellZ;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < 1e-3) dist = 1e-3;

            double norm = dist / cell.Radius;
            double falloff = Math.Exp(-norm * norm); // gaussian
            if (falloff < 1e-4) continue;

            // Radial unit (center → sample) and CCW tangent.
            double rx = dx / dist;
            double rz = dz / dist;
            double tx = -rz;
            double tz = rx;

            // windCell = s * falloff * (cos(inflow)*tangent - sin(inflow)*radial)
            //   low  (s>0): CCW tangent + inflow toward center  (cyclonic)
            //   high (s<0): CW tangent  + outflow from center   (anticyclonic)
            double scale = cell.Strength * falloff;
            wx += scale * (cosIn * tx - sinIn * rx);
            wz += scale * (cosIn * tz - sinIn * rz);
        }

        return new Vec2d(wx, wz);
    }

    /// <summary>Signed pressure at a position (sum of cell contributions). + = low, - = high.</summary>
    public double PressureAt(double x, double z, double totalHours)
    {
        double p = 0;
        double elapsed = totalHours - AnchorHours;
        for (int i = 0; i < Cells.Count; i++)
        {
            PressureCell cell = Cells[i];
            double dx = x - (cell.X + cell.VelX * elapsed);
            double dz = z - (cell.Z + cell.VelZ * elapsed);
            double dist2 = dx * dx + dz * dz;
            double norm2 = dist2 / (cell.Radius * cell.Radius);
            p += cell.Strength * Math.Exp(-norm2);
        }
        p += ThermalPressureFunc?.Invoke(x, z, totalHours) ?? 0.0;
        return p;
    }
}
