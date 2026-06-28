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

    public static WindField? FieldFor(EnumAppSide side)
        => side == EnumAppSide.Server ? ServerField : ClientField;

    public static DynamicWindConfig? ConfigFor(EnumAppSide side)
        => side == EnumAppSide.Server ? ServerConfig : ClientConfig;
}
