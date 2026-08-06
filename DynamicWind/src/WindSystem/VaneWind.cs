using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind.WindSystem;

/// <summary>
/// Shared occlusion/yaw/speed math for wind-vane block entities (<see cref="Blocks.BlockEntityWeathervane"/>,
/// <see cref="Blocks.BlockEntityRotorvane"/>), so the cave/undergroundWindmills gate and the downwind-yaw /
/// wind-magnitude formulas only live in one place.
/// </summary>
internal static class VaneWind
{
    /// <summary>Same cave/undergroundWindmills gate windmill rotors use (<c>BEBehaviorWindmillRotor.CheckWindSpeed</c>):
    /// a vane buried in a cave or otherwise not sky-exposed catches no wind.</summary>
    public static bool IsOccluded(ICoreAPI api, BlockPos pos)
    {
        return api.World.BlockAccessor.GetLightLevel(pos, EnumLightLevelType.OnlySunLight) < 5
            && api.World.Config.GetString("undergroundWindmills", "false") != "true";
    }

    /// <summary>Yaw (radians) for a vane to point downwind given the wind vector; NaN when calm.</summary>
    public static float TargetYawFor(Vec3d wind)
    {
        if (wind.X * wind.X + wind.Z * wind.Z < 1e-8) return float.NaN; // calm
        // Arrow points downwind (away from the source). The +π/2 corrects the model arrow's actual
        // rendered orientation so it lands on the true downwind bearing (-π/2 was 90° off the other way
        // and pointed into the wind; +π/2 is the correct quarter-turn).
        return (float)(Math.Atan2(wind.X, wind.Z) + Math.PI / 2.0);
    }

    /// <summary>True directional wind magnitude of the given wind vector.</summary>
    public static double SpeedOf(Vec3d wind)
    {
        return Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
    }
}
