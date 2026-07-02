using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Blocks;

/// <summary>
/// Maple branchy-leaf block that drops a samara (maple seed) on the wind. Subclasses vanilla
/// <see cref="BlockLeaves"/> (assigned only to the maple / crimson-king-maple variants via a
/// <c>classByType</c> patch) because the leaf random-tick is dispatched by the engine straight to
/// <see cref="Block.ShouldReceiveServerGameTicks"/>/<see cref="Block.OnServerGameTick"/> — neither is
/// forwarded to block behaviours, so a behaviour could never hook it. We keep vanilla decay ticking and
/// add a very rare, wind- and season-gated seed drop on top.
///
/// Rules:
///  • Local wind speed at the leaf must exceed <see cref="MinWindSpeed"/> (90%).
///  • In season: regular maple (sugar/Norway) late summer → end of October; crimson king maple also in
///    late spring (mid-May → early June). Windows are expressed in hemisphere-aware
///    <see cref="IGameCalendar.GetSeasonRel"/> (0..1) so southern-hemisphere trees drop in their own
///    local autumn/spring.
///  • Very low chance per tick (1 in 10 000), with a per-position cooldown of at least one game hour.
/// </summary>
public class BlockMapleLeaves : BlockLeaves
{
    private const double DropChancePerTick = 0.0001;
    private const double CooldownHours = 1.0;
    private const double MinWindSpeed = 0.1;

    // Season windows in GetSeasonRel (0..1). North: seasonRel == YearRel, so Month = ceil(seasonRel*12);
    // these correspond to the calendar months requested. South is auto-offset half a year by the engine.
    private const float MapleStart = 0.62f, MapleEnd = 0.835f;                // late summer → end of October
    private const float CrimsonSpringStart = 0.37f, CrimsonSpringEnd = 0.45f; // mid-May → early June
    private const float CrimsonFallStart = 0.62f, CrimsonFallEnd = 0.78f;     // late August → early October

    // Marker passed through the random-tick so OnServerGameTick knows this is our drop check, not decay.
    private static readonly object MapleDropMarker = new();

    // Per-position drop cooldown (server, main-thread only). Key = (x, y, z); value = last drop game-hour.
    private static readonly Dictionary<(int, int, int), double> lastDropHour = new();

    private bool isMapleLeaf;
    private bool isCrimson;
    private AssetLocation? seedCode;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        string wood = LastCodePart(0);  // leavesbranchy-<type>-<wood>
        string type = LastCodePart(1);
        isCrimson = wood == "crimsonkingmaple";
        isMapleLeaf = (wood == "maple" || isCrimson) && type.StartsWith("grown");
        if (isMapleLeaf) seedCode = new AssetLocation(Code.Domain, "treeseed-" + wood);
    }

    public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
    {
        // Preserve vanilla decay ticking; additionally, on a rare independent roll, schedule a wind-drop
        // check on the main thread. (Runs off-thread, so only the random roll happens here.)
        bool baseWants = base.ShouldReceiveServerGameTicks(world, pos, offThreadRandom, out extra);
        if (isMapleLeaf && offThreadRandom.NextDouble() < DropChancePerTick)
        {
            extra = MapleDropMarker;
            return true;
        }
        return baseWants;
    }

    public override void OnServerGameTick(IWorldAccessor world, BlockPos pos, object? extra = null)
    {
        if (ReferenceEquals(extra, MapleDropMarker))
        {
            TryWindDrop(world, pos);
            return;
        }
        base.OnServerGameTick(world, pos, extra);
    }

    private void TryWindDrop(IWorldAccessor world, BlockPos pos)
    {
        if (seedCode == null) return;

        // In season for this maple type?
        float seasonRel = world.Calendar.GetSeasonRel(pos);
        if (!InSeason(seasonRel)) return;

        // Local wind must be blowing hard (> 90%). On the server this goes through our directional wind
        // hook, so it reflects the real field magnitude at the leaf.
        if (world.BlockAccessor.GetWindSpeedAt(pos).Length() < MinWindSpeed) return;

        // At least one game hour between drops at any given leaf.
        double now = world.Calendar.TotalHours;
        var key = (pos.X, pos.Y, pos.Z);
        if (lastDropHour.TryGetValue(key, out double last) && now - last < CooldownHours) return;
        lastDropHour[key] = now;
        PruneCooldowns(now);

        Item? seed = world.GetItem(seedCode);
        if (seed == null) return;
        // Spawn just below the leaf; the low-density samara then drifts on the wind via vanilla EntityItem.
        world.SpawnItemEntity(new ItemStack(seed), pos.ToVec3d().Add(0.5, 0.0, 0.5));
    }

    private bool InSeason(float s)
    {
        if (isCrimson)
            return (s >= CrimsonSpringStart && s <= CrimsonSpringEnd) || (s >= CrimsonFallStart && s <= CrimsonFallEnd);
        return s >= MapleStart && s <= MapleEnd;
    }

    /// <summary>Drop expired cooldown entries once the table grows, so it can't accumulate unbounded.</summary>
    private static void PruneCooldowns(double now)
    {
        if (lastDropHour.Count < 4096) return;
        var stale = new List<(int, int, int)>();
        foreach (KeyValuePair<(int, int, int), double> kv in lastDropHour)
            if (now - kv.Value >= CooldownHours) stale.Add(kv.Key);
        foreach ((int, int, int) k in stale) lastDropHour.Remove(k);
    }
}
