using DynamicWind.Blocks;
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
        api.RegisterBlockClass("BlockWeathervane", typeof(BlockWeathervane));
        api.RegisterBlockEntityClass("BlockEntityWeathervane", typeof(BlockEntityWeathervane));
        api.RegisterBlockEntityClass("BlockEntityRotorvane", typeof(BlockEntityRotorvane));
        api.RegisterBlockClass("BlockMapleLeaves", typeof(BlockMapleLeaves));
    }
}
