using System;
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

    // Ellipse shape: the along-stream (minor) axis grows slower than the cross-stream (major) one, so
    // cells get more eccentric as they age, and starts already mildly elliptical at spawn rather than a
    // perfect circle.
    private const double MinorDispersalFactor = 0.4;  // minor-axis growth rate as a fraction of major's
    private const double SpawnMinorRatioMin = 0.75;   // minor/major ratio at spawn: 85% ± 10%
    private const double SpawnMinorRatioMax = 0.95;
    private const double OrientationEaseRate = 0.4;   // per-hour exponential approach to the velocity bearing
    private const double MinSpeedForOrientation = 10.0; // below this (blocks/hour), freeze orientation

    // Merge / displace of overlapping cells.
    private const double OverlapFactor = 0.5;        // overlap if centres are within this × (R_i + R_j)
    private const double MergedMaxStrength = 4.0;    // merged same-type cells may exceed the normal cap, so they're genuinely stronger
    private const double DisplacePush = 200.0;       // blocks a fully-overlapping high shoves a low per merge pass

    // Weather → pressure feedback: the weather map nudges each cell's strength. Clear skies push toward
    // high (−), clouds toward low (+), storms strongly toward low. Cloud cover below this reads as "clear".
    private const double ClearCloudThreshold = 0.4;
    private const double StormForcingGain = 1.5; // extra low-ward push per unit of precipitation (storms)

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
    /// Resolve overlapping cells. Two SAME-type cells (both low or both high) within
    /// <see cref="OverlapFactor"/> of their summed radii merge into one deeper/stronger system (summed
    /// strength, |strength|-weighted centre + velocity, combined radius), so colliding fronts reinforce.
    /// A HIGH overlapping a LOW does NOT merge: the high shoves the low aside (a positional push away
    /// from the high) WITHOUT draining the low's strength — the displaced low keeps its full intensity
    /// and simply moves. Runs after <see cref="Advect"/>, where each cell's stored X/Z is its current
    /// position (anchor == now), so centres compare directly.
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

                double overlapFrac = 1.0 - dist / threshold;
                if (overlapFrac < 0) overlapFrac = 0; else if (overlapFrac > 1) overlapFrac = 1;

                if (a.IsLow == b.IsLow)
                {
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

                    consumed[j] = true;
                    changed = true;
                }
                else
                {
                    // DISPLACE: the high pushes the low away, strength untouched.
                    PressureCell high = a.IsLow ? b : a;
                    PressureCell low = a.IsLow ? a : b;
                    double ux = low.X - high.X;
                    double uz = low.Z - high.Z;
                    double ul = Math.Sqrt(ux * ux + uz * uz);
                    if (ul < 1e-6) { ux = 1; uz = 0; } else { ux /= ul; uz /= ul; }

                    double push = DisplacePush * overlapFrac;
                    low.X += ux * push;
                    low.Z += uz * push;
                    // Velocity nudge only persists when cells don't re-derive velocity from the wind
                    // (AdvectWithWind off); harmless otherwise. The positional push is the real effect.
                    low.VelX += ux * push;
                    low.VelZ += uz * push;
                    changed = true;
                }
            }
        }

        if (!changed) return;

        for (int i = n - 1; i >= 0; i--)
            if (consumed[i]) field.Cells.RemoveAt(i);

        field.AnchorHours = now; // positions are current at 'now'
        Dirty = true;
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
            // storms add a strong extra low-ward push.
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
            if (now >= c.DiesTotalHours || Math.Abs(c.Strength) < MinStrength)
            {
                field.Cells.RemoveAt(i);
                Dirty = true;
            }
        }
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
        // the visible map (the old loaded-chunk extent was only a few hundred blocks → tiny dots far
        // apart). Config radii act as safety clamps.
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
            Orientation = (float)(Math.Atan2(velZ, velX) + Math.PI / 2.0)
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
            Orientation = (float)(Math.Atan2(velZ, velX) + Math.PI / 2.0)
        });
        field.AnchorHours = now;
        Dirty = true;
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
