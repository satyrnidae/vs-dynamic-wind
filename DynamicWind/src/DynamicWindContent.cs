using DynamicWind.Blocks;
using DynamicWind.Entities;
using Vintagestory.API.Common;

namespace DynamicWind;

/// <summary>
/// Universal content registration (loads on both sides, unlike the field/sim systems). Registers the
/// mod's block/block entity classes referenced by blocktype JSON, plus the entity behaviours its asset
/// patches attach to vanilla entities.
/// </summary>
public class DynamicWindContent : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockClass("BlockWeathervane", typeof(BlockWeathervane));
        api.RegisterBlockEntityClass("BlockEntityWeathervane", typeof(BlockEntityWeathervane));
        api.RegisterBlockEntityClass("BlockEntityRotorvane", typeof(BlockEntityRotorvane));
        api.RegisterBlockClass("BlockMapleLeaves", typeof(BlockMapleLeaves));

        // Attached to the vanilla sailboat by assets/dynamicwind/patches/boat-sailed-sailtrim.json.
        api.RegisterEntityBehaviorClass(EntityBehaviorSailTrim.Code, typeof(EntityBehaviorSailTrim));
    }
}
