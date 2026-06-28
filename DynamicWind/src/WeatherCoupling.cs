using System;
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

    private static void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
    {
        if (reentrant) return; // inner call from PressureAt→TerrainThermal→GetClimateAt: leave base climate
        if (climate == null || config == null || field == null) return;
        if (config.StormCouplingStrength <= 0f) return;
        // Only adjust the live/now climate, not worldgen values.
        if (mode == EnumGetClimateMode.WorldGenValues) return;

        double pressure;
        reentrant = true;
        try { pressure = field.PressureAt(pos.X, pos.Z, sapi.World.Calendar.TotalHours); }
        finally { reentrant = false; }
        if (pressure > 1) pressure = 1;
        else if (pressure < -1) pressure = -1;

        double delta = config.StormCouplingStrength * pressure * 0.5;
        float rain = climate.Rainfall + (float)delta;
        climate.Rainfall = rain < 0f ? 0f : (rain > 1f ? 1f : rain);
    }
}
