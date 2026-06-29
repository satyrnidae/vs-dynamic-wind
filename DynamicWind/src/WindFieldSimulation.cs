using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace DynamicWind;

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
    private const double MaxRadius = 6000.0;

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
        if (!initialized)
        {
            lastGameHours = now;
            if (field.Cells.Count == 0) field.AnchorHours = now;
            initialized = true;
            return;
        }

        double dGameHours = now - lastGameHours;
        lastGameHours = now;
        if (dGameHours < 0) dGameHours = 0; // calendar reset safety

        Advect(now, dGameHours);
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
                c.Strength *= (float)Math.Exp(-pd.DispersalRate * DispersalDecayPerHour * dGameHours);
            }

            if (pd.BernoulliStrength > 0f && dGameHours > 0)
            {
                // Faster-than-nominal flow lowers pressure (toward low, +); stagnant air raises it.
                c.Strength += (float)(pd.BernoulliStrength * BernoulliPerHour * (speed[i] - 1.0) * dGameHours);
                if (c.Strength > MaxStrength) c.Strength = (float)MaxStrength;
                else if (c.Strength < -MaxStrength) c.Strength = -(float)MaxStrength;
            }
        }

        field.AnchorHours = now;
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
        if (!TryGetPopulationCenter(out double cx, out double cz)) return;

        // Size cells to cover 25–75% of the loaded-chunk extent, so a few systems span what's loaded
        // (instead of one cell blanketing everything → uniform pressure). Config radii act as safety clamps.
        double lr = LoadedRadiusBlocks();
        float radius = GameMath.Clamp((float)((0.25 + rand.NextDouble() * 0.5) * lr), config.CellRadiusMin, config.CellRadiusMax);

        // Jetstream advection speed in blocks/hour (eastbound), scaled by base strength.
        double advect = (300.0 + rand.NextDouble() * 300.0) * Math.Max(0.2, config.BaseJetstreamStrength);
        double velX = advect;
        double velZ = (rand.NextDouble() - 0.5) * advect * 0.3;

        // Lifetime: lows are shorter-lived/sharper than highs.
        double durationHours = isLow ? (12 + rand.NextDouble() * 24) : (24 + rand.NextDouble() * 48);

        // Spawn to the west so the system tracks east through the populated area over its lifetime.
        double travel = velX * durationHours;
        double startX = cx - travel * (0.4 + rand.NextDouble() * 0.3);
        double startZ = cz + (rand.NextDouble() - 0.5) * radius * 2.0;

        // Highs are spawned as strong as lows: a high starts far to the west and must travel (and slowly
        // decay) across the populated area before it can pair with a low into a front. Starting them weak
        // (the old -0.3..-1.0) left them below the marker/front threshold by the time they arrived, so the
        // overlay showed only lows and never drew a front line.
        float strength = isLow
            ? (float)(0.6 + rand.NextDouble() * 1.0)
            : (float)-(0.6 + rand.NextDouble() * 1.0);

        field.Cells.Add(new PressureCell
        {
            X = startX,
            Z = startZ,
            Strength = strength,
            Radius = radius,
            VelX = velX,
            VelZ = velZ,
            BornTotalHours = now,
            DiesTotalHours = now + durationHours
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
        if (dGameHours <= 0) return;
        if (!config.ThermalSeeding || field.ThermalPressureFunc == null) return;

        // Expected seed attempts scale with elapsed game time (so seeding also speeds up during sleep).
        double expected = ThermalSeedRatePerDay / 24.0 * dGameHours;
        int attempts = (int)expected;
        if (attempts > MaxSpawnPerTick) attempts = MaxSpawnPerTick;
        if (rand.NextDouble() < expected - Math.Floor(expected)) attempts++;
        for (int i = 0; i < attempts; i++) TrySeedOne(now);
    }

    private void TrySeedOne(double now)
    {
        if (!TryGetPopulationCenter(out double cx, out double cz)) return;

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

        float radius = GameMath.Clamp((float)(0.3 * LoadedRadiusBlocks()), config.CellRadiusMin, config.CellRadiusMax);
        float strength = (float)Math.Max(-2.0, Math.Min(2.0, th * SeedGain));
        double jet = (300.0 + rand.NextDouble() * 300.0) * Math.Max(0.2, config.BaseJetstreamStrength);
        double durationHours = 8 + rand.NextDouble() * 16;

        field.Cells.Add(new PressureCell
        {
            X = sx,
            Z = sz,
            Strength = strength,
            Radius = radius,
            VelX = jet,                 // advection re-derives this next tick when enabled
            VelZ = (rand.NextDouble() - 0.5) * jet * 0.3,
            BornTotalHours = now,
            DiesTotalHours = now + durationHours
        });
        field.AnchorHours = now;
        Dirty = true;
    }

    /// <summary>Radius in blocks of the server's loaded-chunk area (MaxChunkRadius × chunk size).</summary>
    private double LoadedRadiusBlocks()
    {
        int chunks = sapi.Server.Config?.MaxChunkRadius ?? 12;
        if (chunks < 1) chunks = 1;
        return chunks * 32.0; // GlobalConstants.ChunkSize
    }

    private bool TryGetPopulationCenter(out double x, out double z)
    {
        x = 0; z = 0;
        int n = 0;
        foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
        {
            Entity e = plr.Entity;
            if (e == null) continue;
            x += e.Pos.X;
            z += e.Pos.Z;
            n++;
        }
        if (n == 0) return false;
        x /= n; z /= n;
        return true;
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
            sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(Snapshot()));
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
            if (data == null) return;
            WindFieldState s = SerializerUtil.Deserialize<WindFieldState>(data);
            if (s?.Cells == null) return;
            field.Cells.Clear();
            field.Cells.AddRange(s.Cells);
            field.AnchorHours = s.TotalHoursAnchor;
            lastGameHours = sapi.World.Calendar.TotalHours;
            initialized = true;
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[dynamicwind] Failed to load wind field, starting fresh: {0}", e);
        }
    }
}
