using Vintagestory.API.Common;

namespace DynamicWind;

/// <summary>
/// Universal content registration (loads on both sides, unlike the field/sim systems). Registers the
/// mod's block entity classes referenced by blocktype JSON.
/// </summary>
public class DynamicWindContent : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockEntityClass("Weathervane", typeof(BlockEntityWeathervane));
    }
}
