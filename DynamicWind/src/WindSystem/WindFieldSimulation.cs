using System;
using System.Collections.Generic;
using DynamicWind.Config;
using DynamicWind.Network;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace DynamicWind.WindSystem;

/// <summary>
/// Server-authoritative pressure-system simulation. Spawns low/high cells to the west of players,
/// advects them west-to-east on the jetstream, expires old ones, and persists the set across saves.
/// Writes directly into the shared <see cref="WindField"/> the hook reads.
/// </summary>
public class WindFieldSimulation
{
    private const string SaveKey = "dynamicwind:state";

    // Dispersal: radius growth + strength decay per game-hour at DispersalRate = 1.
    private const double DispersalSpreadPerHour = 120.0; // blocks/hour radius growth
    private const double DispersalDecayPerHour = 0.15;   // strength e-folding rate
    private const double BernoulliPerHour = 0.5;         // strength push per (speed−1) per hour at strength 1
    private const double MaxStrength = 3.0;
    private const double MinStrength = 0.04;             // below this a cell has dispersed away
    private const double MaxRadius = 32000.0;
    private const double MaxDistanceFromPlayer = 72000.0; // a cell farther than this from EVERY online player despawns

    // Ellipse shape: the along-stream (minor) axis grows slower than the cross-stream (major) one, so
    // cells get more eccentric as they age, and starts already mildly elliptical at spawn rather than a
    // perfect circle.
    private const double MinorDispersalFactor = 0.4;  // minor-axis growth rate as a fraction of major's
    private const double SpawnMinorRatioMin = 0.75;   // minor/major ratio at spawn: 85% ± 10%
    private const double SpawnMinorRatioMax = 0.95;
    private const double OrientationEaseRate = 0.4;   // per-hour exponential approach to the velocity bearing
    private const double MinSpeedForOrientation = 10.0; // below this (blocks/hour), freeze orientation

    // Merge / displace of overlapping cells (same-type only — a low and a high overlapping isn't a
    // meaningful event on its own; real fronts/occlusion are intrinsic to a single low, see ApplyOcclusion).
    private const double OverlapFactor = 0.5;        // overlap if centres are within this × (R_i + R_j)
    private const double MergedMaxStrength = 4.0;    // merged same-type cells may exceed the normal cap, so they're genuinely stronger

    // Storm likelihood: builds slowly over a low's life (its fronts sharpen as the system matures), then
    // spikes/drops around occlusion (see ApplyOcclusion) as the storm winds down.
    private const double StormLikelihoodGrowthPerHour = 0.04;

    // Front reach: each low's cold (poleward) and warm (equatorward) front sides carry an independent
    // radial reach multiplier (PressureCell.ColdFrontReach/WarmFrontReach) that grows every hour — cold
    // faster than warm, since real cold fronts advect/extend faster — so the two sides visibly move and
    // expand at different rates instead of a single symmetric contour.
    private const double ColdReachGrowthPerHour = 0.08;
    private const double WarmReachGrowthPerHour = 0.02;
    private const double MaxFrontReach = 3.0;

    // Occlusion: intrinsic to a single low's own lifecycle — its (faster) cold front catches its
    // (slower) warm front as the cyclone matures, lifting the warm sector and starting the storm's
    // decay. Triggered once ColdFrontReach has outgrown WarmFrontReach by OcclusionReachRatio — a
    // sim-driven event (the cold front has literally caught the warm one), not a fixed age timer or a
    // two-cell collision, since real occlusion isn't triggered by a separate high.
    private const double OcclusionReachRatio = 1.5;      // cold/warm reach ratio that triggers occlusion
    private const double OccludedVisualHours = 8.0;      // how long it renders as the occluded symbol
    private const double OcclusionDeathSpeedup = 0.4;    // remaining lifetime is cut to this fraction — occluded lows die out faster
    private const float OcclusionAftermathStormLikelihood = 0.1f; // storms drop hard once occluded

    // Weather → pressure feedback: the weather map nudges each cell's strength. Clear skies push toward
    // high (−), clouds toward low (+), storms strongly toward low. Cloud cover below this reads as "clear".
    private const double ClearCloudThreshold = 0.4;
    private const double StormForcingGain = 1.5; // extra low-ward push per unit of precipitation (storms)

    // Air-mass temperature: each cell's intrinsic warm/cold character (independent of low/high pressure),
    // set once at spawn from birth latitude + season + random jitter — see ComputeAirMassTemp.
    private const double AirMassLatitudeWeight = 0.6; // poleward (map-edge) birth → colder
    private const double AirMassSeasonWeight = 0.4;   // birth-hemisphere summer → warmer, winter → colder
    private const double AirMassJitter = 0.3;         // ± random spread on top of the above

    // Pressure systems are scattered across a wide region centred on (and leaning west of) the
    // population centre, so several systems span the visible map at once instead of a few tiny cells
    // clustered far to the west. Cell radii are a fraction of this range so neighbours overlap into
    // broad, merging blobs.
    private const double SpawnRangeBlocks = 6000.0; // half-extent of the spawn scatter box
    private const double SpawnUpwindLean = 0.3;     // min upwind offset (fraction of range) along the jet bearing

    // Terrain thermal seeding: spawn mobile cells from terrain hotspots near players.
    private const double ThermalSeedRatePerDay = 14.0;   // seed attempts per game day (terrain reacts faster)
    private const double SeedScanRadius = 1500.0;        // sample box half-extent around population center
    private const double SeedThreshold = 0.25;           // minimum |thermal| to seed a cell
    private const double SeedGain = 1.2;                 // thermal magnitude → seeded cell strength
    private const double SeedMinSeparation = 600.0;      // don't seed atop an existing cell

    private readonly ICoreServerAPI sapi;
    private readonly WindField field;
    private readonly DynamicWindConfig config;
    private readonly Random rand;

    private double lastGameHours;
    private bool initialized;
    private bool loadedPendingReanchor;
    public bool Dirty { get; private set; }

    public WindFieldSimulation(ICoreServerAPI sapi, WindField field, DynamicWindConfig config)
    {
        this.sapi = sapi;
        this.field = field;
        this.config = config;
        rand = new Random();
        // NOTE: Calendar is not available yet during StartServerSide; initialize lazily on first tick.
    }

    public void OnTick(float dt)
    {
        double now = sapi.World.Calendar.TotalHours;
        UpdateSeasonalBias();
        if (loadedPendingReanchor)
        {
            // Cells were just restored from disk. The SAVED anchor (and even the load-time calendar) can
            // lag the first live tick's clock; anchoring to that stale time would make this tick advect +
            // disperse every cell by a huge dt — teleporting them thousands of blocks, ballooning radii to
            // the cap, and slamming strengths to the clamp (exactly the "completely different systems on
            // reload" symptom). Re-anchor to the authoritative current time instead: cells keep their saved
            // positions, treated as current (nothing simulates while the world is unloaded).
            field.AnchorHours = now;
            lastGameHours = now;
            initialized = true;
            loadedPendingReanchor = false;
            return;
        }
        if (!initialized)
        {
            lastGameHours = now;
            field.AnchorHours = now;
            initialized = true;
            return;
        }

        double dGameHours = now - lastGameHours;
        lastGameHours = now;
        if (dGameHours < 0) dGameHours = 0; // calendar reset safety

        Advect(now, dGameHours);
        MergeAndDisplace(now, dGameHours);
        ApplyOcclusion(now, dGameHours);
        ApplyWeatherFeedback(now, dGameHours);
        Expire(now);
        MaybeSpawn(now, dGameHours);
        MaybeSeedThermal(now, dGameHours);
    }

    // Centre of the "Summer" season band from GameCalendar.GetSeason: (0.4685 + 0.726) / 2.
    private const double SummerPeakRel = 0.59725;

    /// <summary>
    /// Recompute the global seasonal N/S jetstream bias from the season + hemisphere at a reference
    /// position: +1 (poleward) at peak summer, −1 (equatorward) at peak winter, with the hemisphere
    /// sign so northern sections blow southwesterly in summer and southern sections northwesterly. The
    /// result is globally uniform (the hemisphere flip and half-year season offset cancel), so a single
    /// reference sample represents the whole world; it is synced to clients via <see cref="WindFieldState"/>.
    /// </summary>
    private void UpdateSeasonalBias()
    {
        if (field.SeasonalBiasMaxAngle <= 0) { field.SeasonalBiasZ = 0; return; }
        try
        {
            var refPos = new BlockPos(sapi.WorldManager.MapSizeX / 2, sapi.World.SeaLevel, sapi.WorldManager.MapSizeZ / 2, 0);
            float seasonRel = sapi.World.Calendar.GetSeasonRel(refPos);
            EnumHemisphere hemi = sapi.World.Calendar.GetHemisphere(refPos);
            double summerness = Math.Cos(2.0 * Math.PI * (seasonRel - SummerPeakRel)); // +1 midsummer, −1 midwinter
            double polewardZdir = hemi == EnumHemisphere.South ? 1.0 : -1.0;            // N pole = −Z, S pole = +Z
            field.SeasonalBiasZ = polewardZdir * summerness;
        }
        catch
        {
            field.SeasonalBiasZ = 0; // hemisphere/season not ready yet → no bias
        }
    }

    /// <summary>Shortest-path angle interpolation: eases <paramref name="from"/> toward <paramref name="to"/>
    /// by fraction <paramref name="t"/>, wrapping through whichever direction is closer.</summary>
    private static double AngleLerp(double from, double to, double t)
    {
        double diff = Math.Atan2(Math.Sin(to - from), Math.Cos(to - from));
        return from + diff * t;
    }

    /// <summary>Weighted circular mean of two angles (vector-sum average, robust to wraparound).</summary>
    private static double WeightedAngleMean(double a, double b, double wa, double wb)
    {
        double x = wa * Math.Cos(a) + wb * Math.Cos(b);
        double z = wa * Math.Sin(a) + wb * Math.Sin(b);
        return Math.Abs(x) < 1e-9 && Math.Abs(z) < 1e-9 ? a : Math.Atan2(z, x);
    }

    /// <summary>
    /// Advance every cell: drift along the local background wind (so the pressure differential moves
    /// with the wind), spread out + fade (dispersal), and lower/raise pressure with wind speed
    /// (Bernoulli). Velocities are stored so the client can extrapolate linearly between snapshots.
    /// Sampled in two passes so cells mutated earlier in the loop don't perturb later samples.
    /// </summary>
    private void Advect(double now, double dGameHours)
    {
        int n = field.Cells.Count;
        if (n == 0) return;

        DynamicWindConfig pd = config; // settings are flat on the config now
        double elapsed = now - field.AnchorHours; // effective-position extrapolation (≈ dGameHours)

        var newX = new double[n];
        var newZ = new double[n];
        var newVX = new double[n];
        var newVZ = new double[n];
        var speed = new double[n];

        // Pass 1: sample the background flow at each cell's current effective position.
        for (int i = 0; i < n; i++)
        {
            PressureCell c = field.Cells[i];
            double ex = c.X + c.VelX * elapsed;
            double ez = c.Z + c.VelZ * elapsed;
            newX[i] = ex;
            newZ[i] = ez;

            if (pd.AdvectWithWind || pd.BernoulliStrength > 0f)
            {
                Vec2d w = field.WindAt(ex, ez, now, i); // background only (excludes this cell)
                speed[i] = w.Length();
                if (pd.AdvectWithWind)
                {
                    newVX[i] = w.X * pd.AdvectionSpeed;
                    newVZ[i] = w.Y * pd.AdvectionSpeed;
                }
                else { newVX[i] = c.VelX; newVZ[i] = c.VelZ; }
            }
            else { newVX[i] = c.VelX; newVZ[i] = c.VelZ; speed[i] = 1.0; }
        }

        // Pass 2: write positions/velocities and evolve strength + radius.
        for (int i = 0; i < n; i++)
        {
            PressureCell c = field.Cells[i];
            c.X = newX[i] + newVX[i] * dGameHours;
            c.Z = newZ[i] + newVZ[i] * dGameHours;
            c.VelX = newVX[i];
            c.VelZ = newVZ[i];

            if (pd.DispersalRate > 0f && dGameHours > 0)
            {
                c.Radius += (float)(pd.DispersalRate * DispersalSpreadPerHour * dGameHours);
                if (c.Radius > MaxRadius) c.Radius = (float)MaxRadius;

                float minor = c.RadiusMinor > 1e-3f ? c.RadiusMinor : c.Radius;
                minor += (float)(pd.DispersalRate * DispersalSpreadPerHour * MinorDispersalFactor * dGameHours);
                c.RadiusMinor = Math.Min(minor, c.Radius); // never overtake the major axis

                c.Strength *= (float)Math.Exp(-pd.DispersalRate * DispersalDecayPerHour * dGameHours);
            }

            if (pd.BernoulliStrength > 0f && dGameHours > 0)
            {
                // Faster-than-nominal flow lowers pressure (toward low, +); stagnant air raises it.
                c.Strength += (float)(pd.BernoulliStrength * BernoulliPerHour * (speed[i] - 1.0) * dGameHours);
                if (c.Strength > MaxStrength) c.Strength = (float)MaxStrength;
                else if (c.Strength < -MaxStrength) c.Strength = -(float)MaxStrength;
            }

            if (dGameHours > 0)
            {
                c.StormLikelihood = Math.Min(1f, c.StormLikelihood + (float)(StormLikelihoodGrowthPerHour * dGameHours));
            }

            // Cold (poleward) front reach grows faster than warm (equatorward) — the two sides move and
            // expand at different rates; see ApplyOcclusion for the sim-driven trigger this feeds.
            if (c.IsLow && dGameHours > 0)
            {
                c.ColdFrontReach = (float)Math.Min(MaxFrontReach, c.ColdFrontReach + ColdReachGrowthPerHour * dGameHours);
                c.WarmFrontReach = (float)Math.Min(MaxFrontReach, c.WarmFrontReach + WarmReachGrowthPerHour * dGameHours);
            }

            // Ease the ellipse's stored orientation toward the cell's own travel bearing, rather than
            // snapping, so the shape rotates smoothly. Frozen (not chased) at near-zero own speed to avoid
            // tracking a numerically noisy direction — common for slow/stalled systems, not a rare case.
            if (dGameHours > 0)
            {
                double ownSpeed = Math.Sqrt(newVX[i] * newVX[i] + newVZ[i] * newVZ[i]);
                if (ownSpeed > MinSpeedForOrientation)
                {
                    double target = Math.Atan2(newVZ[i], newVX[i]) + Math.PI / 2.0;
                    double t = 1.0 - Math.Exp(-OrientationEaseRate * dGameHours);
                    c.Orientation = (float)AngleLerp(c.Orientation, target, t);
                }
            }
        }

        field.AnchorHours = now;
        Dirty = true;
    }

    /// <summary>
    /// Merge overlapping SAME-type cells (both low or both high) within <see cref="OverlapFactor"/> of
    /// their summed radii into one deeper/stronger system (summed strength, |strength|-weighted centre +
    /// velocity, combined radius) — colliding same-type systems reinforce each other. A low overlapping a
    /// high is NOT a merge or a special event: real occlusion is intrinsic to a single low's own
    /// lifecycle (see <see cref="ApplyOcclusion"/>), not triggered by a passing high. Runs after
    /// <see cref="Advect"/>, where each cell's stored X/Z is its current position (anchor == now), so
    /// centres compare directly.
    /// </summary>
    private void MergeAndDisplace(double now, double dGameHours)
    {
        int n = field.Cells.Count;
        if (n < 2) return;

        var consumed = new bool[n];
        bool changed = false;

        for (int i = 0; i < n; i++)
        {
            if (consumed[i]) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (consumed[j]) continue;
                PressureCell a = field.Cells[i];
                PressureCell b = field.Cells[j];
                if (a.IsLow != b.IsLow) continue; // opposite types don't interact here

                double dx = b.X - a.X;
                double dz = b.Z - a.Z;
                double dist = Math.Sqrt(dx * dx + dz * dz);
                // Mean radius rather than the major axis alone: two elongated, jet-aligned cells most
                // commonly approach along their travel axis (their *minor* extent), so a pure major-axis
                // sum over-triggers in the common case.
                double aMean = (a.Radius + (a.RadiusMinor > 1e-3f ? a.RadiusMinor : a.Radius)) / 2.0;
                double bMean = (b.Radius + (b.RadiusMinor > 1e-3f ? b.RadiusMinor : b.Radius)) / 2.0;
                double threshold = OverlapFactor * (aMean + bMean);
                if (threshold <= 0 || dist >= threshold) continue;

                // MERGE into cell a; drop cell b.
                double wa = Math.Abs(a.Strength), wb = Math.Abs(b.Strength);
                double wsum = wa + wb;
                if (wsum < 1e-6) { wa = wb = 0.5; wsum = 1.0; }

                double merged = a.Strength + b.Strength;
                if (merged > MergedMaxStrength) merged = MergedMaxStrength;
                else if (merged < -MergedMaxStrength) merged = -MergedMaxStrength;

                a.X = (a.X * wa + b.X * wb) / wsum;
                a.Z = (a.Z * wa + b.Z * wb) / wsum;
                a.VelX = (a.VelX * wa + b.VelX * wb) / wsum;
                a.VelZ = (a.VelZ * wa + b.VelZ * wb) / wsum;
                a.Strength = (float)merged;
                a.Radius = (float)Math.Min(MaxRadius, Math.Sqrt((double)a.Radius * a.Radius + (double)b.Radius * b.Radius));
                double aMinor = a.RadiusMinor > 1e-3f ? a.RadiusMinor : a.Radius;
                double bMinor = b.RadiusMinor > 1e-3f ? b.RadiusMinor : b.Radius;
                a.RadiusMinor = (float)Math.Min(a.Radius, Math.Sqrt(aMinor * aMinor + bMinor * bMinor));
                a.Orientation = (float)WeightedAngleMean(a.Orientation, b.Orientation, wa, wb);
                a.BornTotalHours = Math.Min(a.BornTotalHours, b.BornTotalHours);
                a.DiesTotalHours = Math.Max(a.DiesTotalHours, b.DiesTotalHours);
                a.AirMassTemp = (float)((a.AirMassTemp * wa + b.AirMassTemp * wb) / wsum);
                a.ColdFrontReach = (float)((a.ColdFrontReach * wa + b.ColdFrontReach * wb) / wsum);
                a.WarmFrontReach = (float)((a.WarmFrontReach * wa + b.WarmFrontReach * wb) / wsum);

                consumed[j] = true;
                changed = true;
            }
        }

        if (!changed) return;

        for (int i = n - 1; i >= 0; i--)
            if (consumed[i]) field.Cells.RemoveAt(i);

        field.AnchorHours = now; // positions are current at 'now'
        Dirty = true;
    }

    /// <summary>
    /// Reach-ratio occlusion for LOW cells: real occlusion is a single cyclone's own faster cold front
    /// catching its slower warm front as the system matures, lifting the warm sector and starting the
    /// storm's decay — not an event triggered by a separate high. Once a low's
    /// <see cref="PressureCell.ColdFrontReach"/> has outgrown its <see cref="PressureCell.WarmFrontReach"/>
    /// by <see cref="OcclusionReachRatio"/> (grown every tick in <see cref="Advect"/>) — i.e. the cold
    /// front has literally caught the warm one — and it hasn't already occluded (latched via
    /// <see cref="PressureCell.OccludedUntilTotalHours"/>), its storm likelihood drops hard, it's tagged
    /// to render as the occluded symbol for a while (see FrontComputer), and its remaining lifetime is
    /// cut short (occluded lows die out faster).
    /// </summary>
    private void ApplyOcclusion(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;
        for (int i = 0; i < field.Cells.Count; i++)
        {
            PressureCell c = field.Cells[i];
            if (!c.IsLow || c.OccludedUntilTotalHours > 0) continue; // highs don't occlude; one-shot latch
            if (c.WarmFrontReach <= 1e-4f) continue;
            if (c.ColdFrontReach / c.WarmFrontReach < OcclusionReachRatio) continue;

            c.StormLikelihood = OcclusionAftermathStormLikelihood; // storm winds down once occluded
            c.OccludedUntilTotalHours = now + OccludedVisualHours;

            double remaining = c.DiesTotalHours - now;
            if (remaining > 0) c.DiesTotalHours = now + remaining * OcclusionDeathSpeedup;

            Dirty = true;
        }
    }

    /// <summary>
    /// Let the weather map feed back onto pressure: sample cloud cover + precipitation at each cell and
    /// nudge its strength. Clear skies push toward high (− strength), clouds toward low (+), and storms
    /// strongly toward low — so a cell's own weather reinforces it (lows that storm deepen; highs that
    /// stay clear strengthen), bounded by the strength clamp + dispersal decay + finite cell lifetime.
    /// Reads the live climate (which already carries the pressure→weather coupling), closing the loop.
    /// </summary>
    private void ApplyWeatherFeedback(double now, double dGameHours)
    {
        if (dGameHours <= 0 || config.WeatherPressureCoupling <= 0f) return;
        int n = field.Cells.Count;
        if (n == 0) return;

        int y = sapi.World.SeaLevel;
        double totalDays = sapi.World.Calendar.TotalDays;
        double elapsed = now - field.AnchorHours;
        var pos = new BlockPos(0, y, 0, 0);

        for (int i = 0; i < n; i++)
        {
            PressureCell c = field.Cells[i];
            pos.X = (int)(c.X + c.VelX * elapsed);
            pos.Z = (int)(c.Z + c.VelZ * elapsed);
            ClimateCondition cc = sapi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues, totalDays);
            if (cc == null) continue;

            double cloud = GameMath.Clamp(cc.RainCloudOverlay, 0f, 1f);
            double precip = GameMath.Clamp(cc.Rainfall, 0f, 1f);
            // Clear (cloud below threshold) → negative (toward high); cloudy → positive (toward low);
            // storms add a strong extra low-ward push (storms deepen the low that's producing them).
            double forcing = (cloud - ClearCloudThreshold) + StormForcingGain * precip;

            c.Strength += (float)(config.WeatherPressureCoupling * forcing * dGameHours);
            if (c.Strength > MaxStrength) c.Strength = (float)MaxStrength;
            else if (c.Strength < -MaxStrength) c.Strength = -(float)MaxStrength;
        }
        Dirty = true;
    }

    private void Expire(double now)
    {
        for (int i = field.Cells.Count - 1; i >= 0; i--)
        {
            PressureCell c = field.Cells[i];
            if (now >= c.DiesTotalHours || Math.Abs(c.Strength) < MinStrength || IsTooFarFromAllPlayers(c.X, c.Z))
            {
                field.Cells.RemoveAt(i);
                Dirty = true;
            }
        }
    }

    /// <summary>
    /// True if every online player is farther than <see cref="MaxDistanceFromPlayer"/> from
    /// (<paramref name="x"/>, <paramref name="z"/>) — a system that's drifted this far off nobody's
    /// business isn't going to be seen or felt, so let it despawn rather than simulate it forever.
    /// No online players at all is NOT "too far" (nothing to cull against yet).
    /// </summary>
    private bool IsTooFarFromAllPlayers(double x, double z)
    {
        double maxDistSq = MaxDistanceFromPlayer * MaxDistanceFromPlayer;
        bool anyPlayer = false;
        foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
        {
            Entity e = plr.Entity;
            if (e == null) continue;
            anyPlayer = true;
            double dx = e.Pos.X - x;
            double dz = e.Pos.Z - z;
            if (dx * dx + dz * dz <= maxDistSq) return false;
        }
        return anyPlayer;
    }

    // Cap spawns/seeds per tick so a huge calendar jump (e.g. sleeping through several days) can't
    // flood the world in one tick, while still letting evolution speed up with world time.
    private const int MaxSpawnPerTick = 12;

    private void MaybeSpawn(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;
        if (field.Cells.Count >= config.MaxActiveCells) return;
        // Expected count scales with elapsed GAME time, so pressure systems evolve on the world clock —
        // and speed up while a player sleeps (the calendar fast-forwards → larger dGameHours per tick).
        SpawnExpected(now, config.LowSpawnRatePerDay / 24.0 * dGameHours, isLow: true);
        SpawnExpected(now, config.HighSpawnRatePerDay / 24.0 * dGameHours, isLow: false);
    }

    /// <summary>Spawn roughly <paramref name="expected"/> cells: the whole part plus a fractional chance.</summary>
    private void SpawnExpected(double now, double expected, bool isLow)
    {
        if (expected <= 0) return;
        int n = (int)expected;
        if (n > MaxSpawnPerTick) n = MaxSpawnPerTick;
        for (int i = 0; i < n; i++) Spawn(now, isLow);
        if (rand.NextDouble() < expected - Math.Floor(expected)) Spawn(now, isLow);
    }

    private void Spawn(double now, bool isLow)
    {
        if (field.Cells.Count >= config.MaxActiveCells) return;
        if (!TryGetSpawnAnchor(out double cx, out double cz)) return;

        // Size cells to a fraction of the spawn range so neighbours overlap into broad blobs that span
        // the visible map. Config radii (CellRadiusMin/Max, user-set) are the only clamp.
        float radius = GameMath.Clamp((float)((0.15 + rand.NextDouble() * 0.2) * SpawnRangeBlocks), config.CellRadiusMin, config.CellRadiusMax);

        // Mean jetstream bearing (seasonally tilted N/S). Systems spawn UPWIND along this bearing and
        // scatter to either side of it, so they drift back down the jet THROUGH the player's area rather
        // than being carried off to the north/south when the stream tilts with the season — the old
        // due-west lean made seasonally-tilted systems sail past the player. The wide cross-stream
        // scatter still spreads systems across the map.
        double jetAng = field.SeasonalBiasMaxAngle * field.SeasonalBiasZ;
        double fx = Math.Cos(jetAng), fz = Math.Sin(jetAng); // downwind unit vector
        double sxn = -fz, szn = fx;                          // cross-stream unit vector

        // Advection speed in blocks/hour along the bearing, scaled by base strength.
        double advect = (300.0 + rand.NextDouble() * 300.0) * Math.Max(0.2, config.BaseJetstreamStrength);
        double velX = fx * advect;
        double velZ = fz * advect;

        // Lifetime: lows are shorter-lived/sharper than highs.
        double durationHours = isLow ? (12 + rand.NextDouble() * 24) : (24 + rand.NextDouble() * 48);

        double along = -SpawnRangeBlocks * (SpawnUpwindLean + rand.NextDouble() * (1.0 - SpawnUpwindLean)); // upwind
        double cross = (rand.NextDouble() - 0.5) * 2.0 * SpawnRangeBlocks;                                  // across the map
        double startX = cx + fx * along + sxn * cross;
        double startZ = cz + fz * along + szn * cross;

        // Highs are spawned as strong as lows: a high starts upwind and must travel (and slowly decay)
        // across the populated area before it can pair with a low into a front. Starting them weak (the
        // old -0.3..-1.0) left them below the marker/front threshold by the time they arrived, so the
        // overlay showed only lows and never drew a front line.
        float strength = isLow
            ? (float)(0.6 + rand.NextDouble() * 1.0)
            : (float)-(0.6 + rand.NextDouble() * 1.0);

        double minorRatio = SpawnMinorRatioMin + rand.NextDouble() * (SpawnMinorRatioMax - SpawnMinorRatioMin);

        field.Cells.Add(new PressureCell
        {
            X = startX,
            Z = startZ,
            Strength = strength,
            Radius = radius,
            VelX = velX,
            VelZ = velZ,
            BornTotalHours = now,
            DiesTotalHours = now + durationHours,
            RadiusMinor = (float)(radius * minorRatio),
            Orientation = (float)(Math.Atan2(velZ, velX) + Math.PI / 2.0),
            StormLikelihood = (float)rand.NextDouble(),
            AirMassTemp = ComputeAirMassTemp(startX, startZ)
        });
        // Re-anchor so the new cell's stored X/Z are valid at the shared anchor.
        field.AnchorHours = now;
        Dirty = true;
    }

    /// <summary>
    /// Spawn a mobile, dispersing pressure cell from a terrain thermal hotspot (hot dry land → low,
    /// deep water → high). These then advect with the wind, so terrain influence is no longer pinned
    /// to the terrain — it drifts and spreads downwind. Rate-limited and kept clear of existing cells.
    /// </summary>
    private void MaybeSeedThermal(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;
        if (!config.ThermalSeeding || field.ThermalPressureFunc == null) return;
        if (field.Cells.Count >= config.MaxActiveCells) return;

        // Expected seed attempts scale with elapsed game time (so seeding also speeds up during sleep).
        double expected = ThermalSeedRatePerDay / 24.0 * dGameHours;
        int attempts = (int)expected;
        if (attempts > MaxSpawnPerTick) attempts = MaxSpawnPerTick;
        if (rand.NextDouble() < expected - Math.Floor(expected)) attempts++;
        for (int i = 0; i < attempts; i++) TrySeedOne(now);
    }

    private void TrySeedOne(double now)
    {
        if (field.Cells.Count >= config.MaxActiveCells) return;
        if (!TryGetSpawnAnchor(out double cx, out double cz)) return;

        double sx = cx + (rand.NextDouble() - 0.5) * 2.0 * SeedScanRadius;
        double sz = cz + (rand.NextDouble() - 0.5) * 2.0 * SeedScanRadius;
        double th = field.ThermalPressureFunc!(sx, sz, now);
        if (Math.Abs(th) < SeedThreshold) return;

        // Don't pile a new cell on top of an existing one.
        double elapsed = now - field.AnchorHours;
        foreach (PressureCell c in field.Cells)
        {
            double dx = (c.X + c.VelX * elapsed) - sx;
            double dz = (c.Z + c.VelZ * elapsed) - sz;
            if (dx * dx + dz * dz < SeedMinSeparation * SeedMinSeparation) return;
        }

        float radius = GameMath.Clamp((float)(0.12 * SpawnRangeBlocks), config.CellRadiusMin, config.CellRadiusMax);
        float strength = (float)Math.Max(-2.0, Math.Min(2.0, th * SeedGain));
        double jet = (300.0 + rand.NextDouble() * 300.0) * Math.Max(0.2, config.BaseJetstreamStrength);
        double durationHours = 8 + rand.NextDouble() * 16;
        double velX = jet;                 // advection re-derives this next tick when enabled
        double velZ = (rand.NextDouble() - 0.5) * jet * 0.3;
        double minorRatio = SpawnMinorRatioMin + rand.NextDouble() * (SpawnMinorRatioMax - SpawnMinorRatioMin);

        field.Cells.Add(new PressureCell
        {
            X = sx,
            Z = sz,
            Strength = strength,
            Radius = radius,
            VelX = velX,
            VelZ = velZ,
            BornTotalHours = now,
            DiesTotalHours = now + durationHours,
            RadiusMinor = (float)(radius * minorRatio),
            Orientation = (float)(Math.Atan2(velZ, velX) + Math.PI / 2.0),
            StormLikelihood = (float)rand.NextDouble(),
            AirMassTemp = ComputeAirMassTemp(sx, sz)
        });
        field.AnchorHours = now;
        Dirty = true;
    }

    /// <summary>
    /// Intrinsic air-mass character in [-1, 1] a cell is born with (see
    /// <see cref="PressureCell.AirMassTemp"/>): colder toward the map's N/S edges (a proxy for
    /// poleward birth), warmer/colder with the birth position's own season (via
    /// <see cref="ICalendar.GetSeasonRel"/>, which already accounts for hemisphere), plus random jitter
    /// so same-latitude, same-season systems still vary — real air masses aren't perfectly uniform.
    /// </summary>
    private float ComputeAirMassTemp(double x, double z)
    {
        double mapHalfZ = Math.Max(1.0, sapi.WorldManager.MapSizeZ / 2.0);
        double poleward = GameMath.Clamp((float)(Math.Abs(z - mapHalfZ) / mapHalfZ), 0f, 1f); // 0 = equator, 1 = map edge

        double seasonWarmth = 0;
        try
        {
            var pos = new BlockPos((int)x, sapi.World.SeaLevel, (int)z, 0);
            float seasonRel = sapi.World.Calendar.GetSeasonRel(pos); // hemisphere-aware
            seasonWarmth = Math.Cos(2.0 * Math.PI * (seasonRel - SummerPeakRel)); // +1 that hemisphere's midsummer, −1 midwinter
        }
        catch { /* hemisphere/season not ready yet — treat as neutral */ }

        double jitter = (rand.NextDouble() * 2.0 - 1.0) * AirMassJitter;
        double t = -AirMassLatitudeWeight * poleward + AirMassSeasonWeight * seasonWarmth + jitter;
        return (float)(t < -1.0 ? -1.0 : (t > 1.0 ? 1.0 : t));
    }

    /// <summary>
    /// Pick a RANDOM online player's position as the anchor for one spawn/seed. Averaging all players
    /// into a single centre would, in multiplayer, drop systems in the empty middle between distant
    /// players; choosing a random player each call instead scatters systems around every player's area
    /// over time (and is identical to "the player" in singleplayer).
    /// </summary>
    private bool TryGetSpawnAnchor(out double x, out double z)
    {
        x = 0; z = 0;
        int count = 0;
        foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
            if (plr.Entity != null) count++;
        if (count == 0) return false;

        int pick = rand.Next(count);
        int i = 0;
        foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
        {
            Entity e = plr.Entity;
            if (e == null) continue;
            if (i == pick) { x = e.Pos.X; z = e.Pos.Z; return true; }
            i++;
        }
        return false;
    }

    public WindFieldState Snapshot()
    {
        Dirty = false;
        return new WindFieldState
        {
            Cells = field.Cells.ToArray(),
            BaseStrength = field.BaseStrength,
            MeanderAmplitude = field.MeanderAmplitude,
            CyclonicInflowAngle = field.CyclonicInflowAngle,
            SeasonalBiasZ = field.SeasonalBiasZ,
            TotalHoursAnchor = field.AnchorHours
        };
    }

    public void Save()
    {
        try
        {
            WindFieldState snap = Snapshot();
            byte[] data = SerializerUtil.Serialize(snap);
            sapi.WorldManager.SaveGame.StoreData(SaveKey, data);
            sapi.Logger.Notification("[dynamicwind] Saved {0} pressure cells ({1} bytes, anchor {2:0.0}h).",
                snap.Cells.Length, data.Length, snap.TotalHoursAnchor);
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[dynamicwind] Failed to persist wind field: {0}", e);
        }
    }

    public void Load()
    {
        try
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
            if (data == null)
            {
                sapi.Logger.Notification("[dynamicwind] No saved wind field found under '{0}' — fresh start.", SaveKey);
                return;
            }
            WindFieldState s = SerializerUtil.Deserialize<WindFieldState>(data);
            if (s?.Cells == null)
            {
                sapi.Logger.Warning("[dynamicwind] Saved wind field deserialized to null cells — fresh start.");
                return;
            }
            field.Cells.Clear();
            field.Cells.AddRange(s.Cells);

            // Migrate cells saved before RadiusMinor/Orientation existed: protobuf defaults a missing
            // field to 0, and the per-tick dispersal growth in Advect ADDS to that rather than resetting
            // it, so without this fixup an old-save cell would render as a near-needle-thin ellipse for
            // its first several in-game hours. Reset to circular (rad1 = rad2) and orient along the
            // current jetstream bearing (the same formula Spawn() uses) rather than the arbitrary default.
            double jetOrientation = field.SeasonalBiasMaxAngle * field.SeasonalBiasZ + Math.PI / 2.0;
            foreach (PressureCell c in field.Cells)
            {
                if (c.RadiusMinor > 1e-3f) continue;
                c.RadiusMinor = c.Radius;
                c.Orientation = (float)jetOrientation;
            }

            // Migrate cells saved before StormLikelihood existed: protobuf defaults a missing field to
            // 0, which would otherwise read as "will never storm" instead of the intended "unknown, so
            // assume the worst" default for pre-existing systems.
            foreach (PressureCell c in field.Cells)
            {
                if (c.StormLikelihood <= 0f) c.StormLikelihood = 1f;
            }

            // Migrate cells saved before ColdFrontReach/WarmFrontReach existed: protobuf defaults a
            // missing field to 0, which would collapse the front contour to nothing instead of the
            // intended neutral 1 (matches the base ellipse, no asymmetry yet).
            foreach (PressureCell c in field.Cells)
            {
                if (c.ColdFrontReach <= 0f) c.ColdFrontReach = 1f;
                if (c.WarmFrontReach <= 0f) c.WarmFrontReach = 1f;
            }

            field.AnchorHours = s.TotalHoursAnchor; // provisional; re-anchored to the live clock next tick
            loadedPendingReanchor = true;           // defer time baseline to the first live tick (see OnTick)
            sapi.Logger.Notification("[dynamicwind] Loaded {0} pressure cells (saved anchor {1:0.0}h); re-anchoring to live clock.",
                field.Cells.Count, field.AnchorHours);
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[dynamicwind] Failed to load wind field, starting fresh: {0}", e);
        }
    }
}
