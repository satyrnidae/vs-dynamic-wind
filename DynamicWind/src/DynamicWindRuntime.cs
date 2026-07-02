using DynamicWind.Config;
using DynamicWind.WindSystem;
using Vintagestory.API.Common;

namespace DynamicWind;

/// <summary>
/// Static bridge so Harmony patches (which can't be handed instance references) can reach the active
/// wind field and config for their side. In singleplayer both the integrated server and the client
/// live in one process, so both pairs may be populated; patches pick by entity/world side.
/// </summary>
public static class DynamicWindRuntime
{
    public static WindField? ServerField;
    public static DynamicWindConfig? ServerConfig;

    public static WindField? ClientField;
    public static DynamicWindConfig? ClientConfig;

    /// <summary>The client mod system instance, so client-only callers (e.g. <see cref="Client.MapLayers.WindArrowMapLayer"/>)
    /// can reach <see cref="DynamicWindModCommon.ComputeWindSpeed"/> without their own copy of the formula.</summary>
    public static DynamicWindModCommon? ClientModSystem;

    public static WindField? FieldFor(EnumAppSide side)
        => side == EnumAppSide.Server ? ServerField : ClientField;

    public static DynamicWindConfig? ConfigFor(EnumAppSide side)
        => side == EnumAppSide.Server ? ServerConfig : ClientConfig;
}
