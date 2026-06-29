using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace DynamicWind;

/// <summary>
/// Couples weather to the pressure field using the public <c>OnGetClimate</c> event (no fragile
/// internal patch): inside low-pressure cells rainfall is pushed up so the vanilla weather system
/// selects cloudier/stormier patterns there; inside highs it is pushed down toward clear skies.
/// Strength of the effect is scaled by <see cref="DynamicWindConfig.StormCouplingStrength"/>.
/// (The wind-strength side of "storms = stronger, swirlier wind" is already inherent in the field,
/// since low cells add circulation.)
/// </summary>
internal static class WeatherCoupling
{
    private static ICoreServerAPI sapi = null!;
    private static WindField field = null!;
    private static DynamicWindConfig config = null!;
    private static bool registered;
    // PressureAt → TerrainThermal → GetClimateAt re-enters this handler; guard against recursion.
    [ThreadStatic] private static bool reentrant;

    // Fronts (recomputed server-side each tick); swapped atomically so OnGetClimate can read lock-free.
    private static volatile List<FrontLine> fronts = new();
    private const double FrontRainBand = 250.0; // blocks from the front line that rain reaches
    private const double FrontRainStrength = 0.6;

    /// <summary>Replace the active front set (called from the server tick).</summary>
    public static void SetFronts(List<FrontLine> f) => fronts = f ?? new List<FrontLine>();

    public static void Init(ICoreServerAPI api, WindField windField, DynamicWindConfig cfg, ILogger logger)
    {
        sapi = api;
        field = windField;
        config = cfg;
        if (!registered)
        {
            api.Event.OnGetClimate += OnGetClimate;
            registered = true;
            logger.Notification("[dynamicwind] Weather coupling enabled (pressure → rainfall).");
        }
    }

    public static void UpdateConfig(DynamicWindConfig cfg) => config = cfg;

    /// <summary>
    /// Extra rainfall (0..1) from being on the high-pressure side of a nearby front. For each front the
    /// position is projected onto the line (must be within its span) and onto the low→high axis; only the
    /// high side (positive) within <see cref="FrontRainBand"/> contributes, strongest at the line.
    /// </summary>
    private static double FrontRainAt(double x, double z)
    {
        List<FrontLine> fr = fronts;
        double best = 0;
        for (int i = 0; i < fr.Count; i++)
        {
            FrontLine f = fr[i];
            double rx = x - f.MidX, rz = z - f.MidZ;
            double along = rx * f.DirX + rz * f.DirZ;
            if (Math.Abs(along) > f.Length * 0.5) continue;
            double side = rx * f.AxisX + rz * f.AxisZ; // >0 = high-pressure side
            if (side <= 0 || side > FrontRainBand) continue;
            double prox = 1.0 - side / FrontRainBand;
            if (prox > best) best = prox;
        }
        return best;
    }

    private static void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
    {
        if (reentrant) return; // inner call from PressureAt→TerrainThermal→GetClimateAt: leave base climate
        if (climate == null || config == null || field == null) return;
        if (config.StormCouplingStrength <= 0f) return;
        // Only adjust the live/now climate, not worldgen values.
        if (mode == EnumGetClimateMode.WorldGenValues) return;

        double pressure;
        reentrant = true;
        try { pressure = field.PressureAt(pos.X, pos.Y, pos.Z, sapi.World.Calendar.TotalHours); }
        finally { reentrant = false; }
        if (pressure > 1) pressure = 1;
        else if (pressure < -1) pressure = -1;

        // Pressure rain: more in lows, less in highs.
        double delta = config.StormCouplingStrength * pressure * 0.5;
        // Front rain: forms on the HIGH-pressure side of a front, within a band of the front line.
        delta += config.StormCouplingStrength * FrontRainStrength * FrontRainAt(pos.X, pos.Z);

        float rain = climate.Rainfall + (float)delta;
        climate.Rainfall = rain < 0f ? 0f : (rain > 1f ? 1f : rain);
    }
}
