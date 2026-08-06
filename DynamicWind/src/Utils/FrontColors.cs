using Vintagestory.API.MathTools;

namespace DynamicWind.Utils;

/// <summary>
/// Shared color palette for weather-front vector rendering (<see cref="DynamicWind.Client.MapLayers.FrontMapLayer"/>).
/// Both <see cref="ColdBlue"/> and <see cref="WarmCrimson"/> are the same cobalt/crimson used to tint
/// high/low-pressure wind arrows (<c>WindArrowMapLayer.HighColor</c>/<c>LowColor</c>) - fronts sit on the
/// same pressure cells, so these are deliberate shared constants, not a coincidence. <see cref="OccludedPurple"/>
/// is <see cref="ColdBlue"/> and <see cref="WarmCrimson"/> component-averaged, approximating the deep
/// alizarin purple a real cold + warm front mix reads as.
/// </summary>
public static class FrontColors
{
    public static readonly Vec4f ColdBlue = new(0x26 / 255f, 0x51 / 255f, 0xFF / 255f, 1f);
    public static readonly Vec4f WarmCrimson = new(0xFF / 255f, 0x10 / 255f, 0x00 / 255f, 1f);
    public static readonly Vec4f OccludedPurple = new(0x93 / 255f, 0x31 / 255f, 0x80 / 255f, 1f);
}
