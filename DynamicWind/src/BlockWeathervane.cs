using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind;

/// <summary>
/// Weathervane block. A weathervane needs something to stand on, so it may only be placed when the
/// block directly below presents a solid top surface over at least the central 2×2 voxels of the face.
/// The check is delegated to the supporting block's <see cref="Block.CanAttachBlockAt"/> with an
/// attachment area covering those centre voxels (exactly how vanilla floor-standing decorations — oil
/// lamps, lanterns — gate placement), so chiselled/partial blocks are evaluated voxel-precisely while
/// ordinary full blocks fall back to their <c>SideSolid[UP]</c> flag.
/// </summary>
public class BlockWeathervane : Block
{
    // Central 2×2 voxels of the 16×16 top face (voxels 7 and 8 on each axis), at the very top layer.
    // Coordinates are in voxel dimensions (0..16); see Block.CanAttachBlockAt's attachmentArea param.
    private static readonly Cuboidi CenterTopArea = new(8, 15, 8, 9, 16, 9);

    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (!base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

        if (!HasSupport(world.BlockAccessor, blockSel.Position))
        {
            failureCode = "requiresolidground";
            return false;
        }

        return true;
    }

    /// <summary>
    /// When the supporting block below changes, "pop" the weathervane (break + drop as an item) if it no
    /// longer presents the required central 2×2 solid top — e.g. the block is removed, or its top voxels
    /// are chiselled away. Same self-dropping behaviour as torches/lanterns
    /// (<c>BlockGroundAndSideAttachable.OnNeighbourBlockChange</c>).
    /// </summary>
    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
    {
        if (!HasSupport(world.BlockAccessor, pos))
            world.BlockAccessor.BreakBlock(pos, null);
        else
            base.OnNeighbourBlockChange(world, pos, neibpos);
    }

    /// <summary>True if the block directly below supports a weathervane (solid over the central 2×2 of its top face).</summary>
    private bool HasSupport(IBlockAccessor blockAccessor, BlockPos pos)
    {
        BlockPos below = pos.DownCopy();
        Block belowBlock = blockAccessor.GetBlock(below);
        return belowBlock.CanAttachBlockAt(blockAccessor, this, below, BlockFacing.UP, CenterTopArea);
    }
}
