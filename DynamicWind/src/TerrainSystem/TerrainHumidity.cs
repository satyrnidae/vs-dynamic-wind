using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind.TerrainSystem;

/// <summary>
/// Terrain-noise humidity baseline: reads the worldgen rainfall noise map vanilla already generates
/// (<see cref="ClimateCondition.WorldgenRainfall"/>, stable 0..1, unaffected by live weather) plus a
/// small forest-density contribution (denser forest retains more moisture), giving
/// <see cref="WindSystem.WindField.HumidityAt"/> a terrain-shaped starting point before the pressure/noise
/// overlays are added. Same code runs on server and client; far/ungenerated columns return a neutral 0.5.
///
/// Safe to call from inside <see cref="DynamicWindModCommon.OnGetClimate"/>'s reentrancy guard:
/// <see cref="EnumGetClimateMode.WorldGenValues"/> reads return immediately at the top of that handler
/// (mirrors the reasoning already documented for <see cref="TerrainThermal"/>/<c>ThermalPressureFunc</c>).
/// </summary>
internal static class TerrainHumidity
{
    private const double ForestWeight = 0.15; // small nudge — the worldgen rainfall noise already dominates

    public static double WorldgenBaselineAt(IWorldAccessor world, double x, double z)
    {
        var pos = new BlockPos((int)x, 0, (int)z, 0);
        // totalDays is unused by WorldGenValues mode (see IBlockAccessor.GetClimateAt docs), so 0 is fine.
        ClimateCondition cc = world.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.WorldGenValues, 0.0);
        if (cc == null) return 0.5;

        double rainfall = GameMath.Clamp(cc.WorldgenRainfall, 0f, 1f);
        double forest = GameMath.Clamp(cc.ForestDensity, 0f, 1f);
        return GameMath.Clamp(rainfall + ForestWeight * (forest - 0.5), 0.0, 1.0);
    }
}
