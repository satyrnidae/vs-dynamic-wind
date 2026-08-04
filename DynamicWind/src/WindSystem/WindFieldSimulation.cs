using System;
using System.Collections.Generic;
using System.Threading;
using DynamicWind.Config;
using DynamicWind.Network;
using DynamicWind.PressureSystem;
using DynamicWind.TerrainSystem;
using DynamicWind.Utils;
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
///
/// Runs on its own dedicated thread (see <see cref="Start"/>/<see cref="Stop"/>) rather than the main
/// server tick — the per-cell math (advection, occlusion's O(n^2) neighbour search, weather-feedback
/// climate sampling) is the dominant cost of this mod and has no reason to compete with the main tick
/// for CPU time. World queries here (Calendar, GetClimateAt via ThermalCache, the terrain sampler) are
/// already tolerated off the main thread elsewhere in this mod (see ThermalCache's own thread-safety
/// note — item physics already queries wind, and therefore these same lookups, off-thread). The one
/// genuinely unsafe piece — enumerating <c>IWorldAccessor.AllOnlinePlayers</c>/<c>NearestPlayer</c>
/// concurrently with players joining/leaving on the main thread — is avoided by having the main thread
/// snapshot player positions once per tick (see <see cref="CapturePlayerSnapshot"/>) into an immutable
/// array the sim thread reads lock-free.
/// </summary>
public class WindFieldSimulation
{
    private const string SaveKey = "dynamicwind:state";

    // Dispersal: radius growth + strength decay per game-hour at DispersalRate = 1. Decay was slowed
    // (0.15 → 0.05) alongside the larger CellRadius/SpawnDistanceBlocks scale-up: at the old rate a
    // cell's natural decay-driven lifespan (~50-90h at default DispersalRate) fell well short of the
    // ~95-190h it now takes to advect in from the farther spawn distance, so systems faded out long
    // before ever reaching a player — leaving the jetstream's own circulation/direction effectively
    // unopposed. At 0.05 the decay-driven lifespan (~150-280h) comfortably covers that travel time.
    private const double DispersalSpreadPerHour = 120.0; // blocks/hour radius growth
    private const double DispersalDecayPerHour = 0.05;   // strength e-folding rate
    private const double BernoulliPerHour = 0.5;         // strength push per (speed−1) per hour at strength 1
    private const double MaxStrength = 3.0;
    private const double MinStrength = 0.04;             // below this a cell has dispersed away
    private const double MaxRadius = 80000.0;
    private const double DespawnMargin = 30000.0; // a cell despawns once it's farther than this PLUS its own radius (toward the player) from EVERY online player

    // Former DynamicWindConfig tunables, now fixed constants (see DynamicWindConfig's class remarks).
    private const float LowSpawnRatePerDay = 1.5f;
    private const float HighSpawnRatePerDay = 1.0f;
    private const float CellRadiusMin = 15000f;
    private const float CellRadiusMax = 30000f;
    private const float WeatherPressureCoupling = 0.3f;
    private const float AdvectionSpeed = 400f;
    private const float DispersalRate = 0.3f;
    private const float BernoulliStrength = 0.3f;

    // Velocity easing (AdvectWithWind): a cell doesn't snap straight to the local background wind each
    // tick, it exponentially eases its own velocity toward that target — real inertia, so a cell responds
    // to nearby circulation and the base jetstream over several hours rather than jumping to a new
    // velocity on a single tick.
    private const double VelocityEaseRatePerHour = 0.15;

    // Air-mass movement bias: a cell's own AirMassTemp rotates its target advection bearing toward the
    // pole (warm) or equator (cold) side — the same shape as WindField.SeasonalBiasZ tilting the base
    // jetstream angle, so "poleward"/"equatorward" stay meaningful relative to whatever the local flow is
    // actually doing rather than a flat Z-axis push. See AirMassBiasedAngle.
    private const double AirMassTempMoveBiasMaxAngle = 25.0 * Math.PI / 180.0; // ~25° at |AirMassTemp|=1

    // Ellipse shape: the along-stream (minor) axis grows slower than the cross-stream (major) one, so
    // cells get more eccentric as they age, and starts already mildly elliptical at spawn rather than a
    // perfect circle.
    private const double MinorDispersalFactor = 0.4;  // minor-axis growth rate as a fraction of major's
    private const double SpawnMinorRatioMin = 0.75;   // minor/major ratio at spawn: 85% ± 10%
    private const double SpawnMinorRatioMax = 0.95;
    private const double OrientationEaseRate = 0.4;   // per-hour exponential approach to the velocity bearing
    private const double MinSpeedForOrientation = 10.0; // below this (blocks/hour), freeze orientation

    // Storm likelihood: builds slowly over a low's life (its fronts sharpen as the system matures), then
    // spikes/drops around occlusion (see ApplyOcclusion) as the storm winds down.
    private const double StormLikelihoodGrowthPerHour = 0.04;

    // Front reach: each low's cold (equatorward) and warm (poleward) front sides carry an independent
    // radial reach multiplier (PressureCell.ColdFrontReach/WarmFrontReach) that grows every hour — cold
    // faster than warm at the base rate, since real cold fronts advect/extend faster — so the two sides
    // visibly move and expand at different rates instead of a single symmetric contour. The base rate on
    // each side is further modulated by DeltaTSlowdownScale in ApplyOcclusion (a similar air mass ahead
    // offers little resistance — the front sharpens/advances near its base rate — a starkly different one
    // slows it).
    private const double ColdReachGrowthPerHour = 0.08;
    private const double WarmReachGrowthPerHour = 0.02;
    private const double MaxFrontReach = 3.0;

    // Occlusion: a single low's (faster) cold front physically catching up to another pressure cell (low
    // or high) on its equatorward side — a real two-cell event, not a fixed ratio against its own warm
    // front or an age timer. The cold front's reach must clear OcclusionCatchMinReach before a catch can
    // trigger (a freshly-spawned cell's own radius can already overlap a neighbour at reach=1, since
    // cells are deliberately sized to overlap into broad blobs — this guards against an instant catch).
    // Growth rate on a side scales with |ΔAirMassTemp| to whatever cell sits there: a small delta (similar
    // air masses) barely slows the base rate; a large one (starkly different air masses) slows it a lot —
    // see DeltaTSlowdownScale.
    private const float OcclusionCatchMinReach = 1.15f;
    private const double OcclusionCatchRadiusFactor = 0.6;       // catch when dist <= this × ColdFrontReach × own radius + ...
    private const double OcclusionCatchTargetRadiusFactor = 0.3; // ... this × the caught cell's own radius
    private const float OcclusionNeighbourMinStrength = 0.15f;   // ignore trivial/dying cells as catch targets or reach-growth references
    private const double DeltaTSlowdownScale = 1.5;               // reach growth divided by (1 + this × |ΔAirMassTemp|)
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
    // clustered far to the west. Spawn-time radius is sampled directly from [CellRadiusMin, CellRadiusMax]
    // rather than from a fixed scale reference, so it stays correct under user retuning instead of
    // drifting relative to a hardcoded constant.

    // Spawn placement: a new front starts far out from a random player — like a real system approaching
    // from a distance — generally upwind so it drifts back through the player's area, scattered across a
    // spread of bearings rather than pinned to the exact upwind line. Distance is capped by the cell's own
    // radius so it never lands close enough to the DespawnMargin threshold to vanish the same tick it
    // spawns (see IsTooFarFromAllPlayers) — a bigger cell can safely spawn a little farther out.
    private const double SpawnDistanceBlocks = 40000.0;      // target distance from the anchor player
    private const double SpawnDistanceJitter = 8000.0;       // ± random spread on top of the target
    private const double SpawnDespawnSafetyMargin = 10000.0; // stay this far inside the despawn threshold
    private const double SpawnAngleSpreadRad = Math.PI / 3.0; // ± scatter around the pure-upwind bearing
    private const double SpawnPlayerAvoidRadius = 6000.0;    // never spawn this close to ANY online player
    private const double SpawnPlayerAvoidPush = 1500.0;      // extra clearance pushed past the avoid radius

    // NOTE: terrain-thermal seeding (mobile cells spawned from water/terrain hotspots) and the
    // heat-wave/cold-wave events built on the same siting path were both REMOVED — every mobile cell now
    // comes from the jetstream spawner alone. TerrainThermal no longer feeds pressure at all: it is a
    // TEMPERATURE field driving the sea breeze directly (WindField.SeaBreezeStrength).

    // How often the dedicated sim thread ticks — matches the old main-thread tick listener interval.
    private const int SimIntervalMs = 2000;

    private readonly ICoreServerAPI sapi;
    private readonly WindField field;
    private readonly DynamicWindConfig config;
    private readonly Random rand;

    private double lastGameHours;
    private bool initialized;
    private bool loadedPendingReanchor;
    private volatile bool dirty;
    public bool Dirty { get => dirty; private set => dirty = value; }

    // Immutable snapshot of online-player X/Z positions, refreshed once per main-thread tick (see
    // CapturePlayerSnapshot) and read lock-free by the sim thread — the array reference swap is the only
    // publication needed since the array itself is never mutated after being built.
    private volatile PlayerPos[] playerSnapshot = Array.Empty<PlayerPos>();

    private readonly struct PlayerPos
    {
        public readonly double X, Z;
        public PlayerPos(double x, double z) { X = x; Z = z; }
    }

    // Nobody online (e.g. between CapturePlayerSnapshot's first call and a player actually joining, or a
    // dedicated server sitting empty) ⇒ nothing to spawn near or despawn against — spawn/seed/despawn all
    // abort up front rather than falling through to per-cell/per-attempt checks that always fail anyway.
    private bool HasPlayers => playerSnapshot.Length > 0;

    private Thread? simThread;
    private volatile bool running;

    public WindFieldSimulation(ICoreServerAPI sapi, WindField field, DynamicWindConfig config)
    {
        this.sapi = sapi;
        this.field = field;
        this.config = config;
        rand = new Random();
        // NOTE: Calendar is not available yet during StartServerSide; initialize lazily on first tick.
    }

    /// <summary>Main-thread only: refreshes the player-position snapshot the sim thread reads. Call once
    /// per main server tick.</summary>
    public void CapturePlayerSnapshot()
    {
        var list = new List<PlayerPos>();
        foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
        {
            Entity e = plr.Entity;
            if (e != null) list.Add(new PlayerPos(e.Pos.X, e.Pos.Z));
        }
        playerSnapshot = list.ToArray();
    }

    /// <summary>Starts the dedicated sim thread. Call once, after the first <see cref="CapturePlayerSnapshot"/>
    /// / <see cref="Load"/> so it doesn't run its first tick against an empty player snapshot or a
    /// not-yet-restored cell list.</summary>
    public void Start()
    {
        if (simThread != null) return;
        running = true;
        simThread = TyronThreadPool.CreateDedicatedThread(SimLoop, "dynamicwind-sim");
        simThread.Start();
    }

    public void Stop()
    {
        running = false;
        simThread?.Join(SimIntervalMs * 2);
        simThread = null;
    }

    private void SimLoop()
    {
        while (running)
        {
            try { OnTick(); }
            catch (Exception e)
            {
                sapi.Logger.Error("[dynamicwind] Sim tick threw, skipping this tick: {0}", e);
            }
            Thread.Sleep(SimIntervalMs);
        }
    }

    public void OnTick()
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
            // The drift integral carries its own clock, so it needs the same treatment: leaving it at the
            // saved timestamp would make CloudDriftAt extrapolate across the entire offline gap and jump
            // the precipitation texture thousands of blocks on the first frame after a load.
            field.CloudDriftHours = now;
            lastGameHours = now;
            initialized = true;
            loadedPendingReanchor = false;
            return;
        }
        if (!initialized)
        {
            lastGameHours = now;
            field.AnchorHours = now;
            field.CloudDriftHours = now;
            initialized = true;
            return;
        }

        double dGameHours = now - lastGameHours;
        lastGameHours = now;
        if (dGameHours < 0) dGameHours = 0; // calendar reset safety

        if (!HasPlayers)
        {
            // Pause the sim entirely when nobody's online — same idea as the world tick itself pausing:
            // no advection, occlusion, weather feedback, spawning, or despawning happens. Still re-anchor
            // so the stale gap doesn't accumulate: without this, once the field's anchor sat idle for a
            // long real-world stretch, the next Advect would extrapolate every cell's effective position
            // across that entire gap using its last-known velocity, then jump it there in one tick — the
            // same "teleporting on reload" failure loadedPendingReanchor exists to avoid, just for an
            // empty-server gap instead of a save/load gap. Cells keep their frozen X/Z; only the anchor
            // moves, so nothing actually appears to move until the sim resumes.
            lock (field.CellsLock) { field.AnchorHours = now; }
            field.CloudDriftHours = now; // same reasoning as the reload re-anchor above
            return;
        }

        AccumulateCloudDrift(now, dGameHours);
        Advect(now, dGameHours);
        ApplyOcclusion(now, dGameHours);
        ApplyWeatherFeedback(now, dGameHours);
        Expire(now);
        MaybeSpawn(now, dGameHours);
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

    // Cloud-texture drift. Blocks per game-hour per unit of wind magnitude — the speed vanilla's
    // precipitation noise is dragged across the world. Matched to AdvectionSpeed's scale (cells and the
    // cloud field should travel together; a storm's rain band shouldn't lag its own low by 30×) but kept
    // separate so it can be tuned without moving the pressure systems themselves.
    private const double CloudDriftSpeed = 400.0;

    /// <summary>
    /// Integrate the cloud-texture drift one tick. Sampled from the GLOBAL base flow at a reference
    /// position, not the local wind: the offset is applied uniformly to every precipitation-noise lookup,
    /// so a position-dependent value would shear/tear the noise field rather than translate it. Uses the
    /// same jetstream bearing the base field does (meander + seasonal tilt), so the clouds track the
    /// prevailing synoptic flow.
    ///
    /// Accumulated here rather than derived on demand because it is a TIME INTEGRAL: the flow's bearing
    /// changes over the year, and any closed-form "bearing × elapsed" shortcut would jump the entire
    /// pattern whenever the bearing moved.
    /// </summary>
    private void AccumulateCloudDrift(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;

        double refX = sapi.WorldManager.MapSizeX * 0.5;
        double refZ = sapi.WorldManager.MapSizeZ * 0.5;
        double meander = field.MeanderAmplitude * WindField.Meander(refX, refZ, now, field.MeanderFrequency);
        double angle = meander + field.SeasonalBiasMaxAngle * field.SeasonalBiasZ;
        double speed = field.BaseStrength * CloudDriftSpeed;

        field.CloudDriftVelX = Math.Cos(angle) * speed;
        field.CloudDriftVelZ = Math.Sin(angle) * speed;
        field.CloudDriftX += field.CloudDriftVelX * dGameHours;
        field.CloudDriftZ += field.CloudDriftVelZ * dGameHours;
        field.CloudDriftHours = now;
        Dirty = true;
    }

    /// <summary>
    /// Advance every cell: drift along the local background wind (so the pressure differential moves
    /// with the wind), spread out + fade (dispersal), and lower/raise pressure with wind speed
    /// (Bernoulli). Velocities are stored so the client can extrapolate linearly between snapshots.
    /// Sampled in two passes so cells mutated earlier in the loop don't perturb later samples.
    /// </summary>
    private void Advect(double now, double dGameHours)
    {
        lock (field.CellsLock)
        {
            int n = field.Cells.Count;
            if (n == 0) return;

            bool advectWithWind = config.AdvectWithWind;
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

                // BernoulliStrength is a fixed positive constant, so the background wind is always sampled
                // (needed for both advection and the Bernoulli speed/pressure coupling below).
                Vec2d w = field.WindAt(ex, ez, now, i); // background only (excludes this cell)
                speed[i] = w.Length();
                if (advectWithWind)
                {
                    // Ease toward the local wind rather than snapping to it — see VelocityEaseRatePerHour.
                    // The target bearing is also rotated toward the pole (warm) or equator (cold) side by
                    // this cell's own AirMassTemp — see AirMassBiasedAngle.
                    double baseAngle = Math.Atan2(w.Y, w.X);
                    double biasedAngle = AirMassBiasedAngle(baseAngle, ez, c.AirMassTemp);
                    double mag = speed[i] * AdvectionSpeed; // speed[i] == w.Length(), sampled just above
                    double targetVX = Math.Cos(biasedAngle) * mag;
                    double targetVZ = Math.Sin(biasedAngle) * mag;
                    double ease = 1.0 - Math.Exp(-VelocityEaseRatePerHour * dGameHours);
                    newVX[i] = c.VelX + (targetVX - c.VelX) * ease;
                    newVZ[i] = c.VelZ + (targetVZ - c.VelZ) * ease;
                }
                else { newVX[i] = c.VelX; newVZ[i] = c.VelZ; }
            }

            // Pass 2: write positions/velocities and evolve strength + radius.
            for (int i = 0; i < n; i++)
            {
                PressureCell c = field.Cells[i];
                c.X = newX[i] + newVX[i] * dGameHours;
                c.Z = newZ[i] + newVZ[i] * dGameHours;
                c.VelX = newVX[i];
                c.VelZ = newVZ[i];

                ApplyDispersal(c, dGameHours);
                ApplyBernoulli(c, speed[i], dGameHours);
                GrowStormLikelihood(c, dGameHours);
                EaseOrientation(c, newVX[i], newVZ[i], dGameHours);
            }

            field.AnchorHours = now;
            Dirty = true;
        }
    }

    /// <summary>Radius/strength dispersal — see DispersalRate/DispersalSpreadPerHour/DispersalDecayPerHour.</summary>
    private static void ApplyDispersal(PressureCell c, double dGameHours)
    {
        if (dGameHours <= 0) return;

        c.Radius += (float)(DispersalRate * DispersalSpreadPerHour * dGameHours);
        if (c.Radius > MaxRadius) c.Radius = (float)MaxRadius;

        float minor = c.RadiusMinor > 1e-3f ? c.RadiusMinor : c.Radius;
        minor += (float)(DispersalRate * DispersalSpreadPerHour * MinorDispersalFactor * dGameHours);
        c.RadiusMinor = Math.Min(minor, c.Radius); // never overtake the major axis

        c.Strength *= (float)Math.Exp(-DispersalRate * DispersalDecayPerHour * dGameHours);
    }

    /// <summary>Faster-than-nominal flow lowers pressure (toward low, +); stagnant air raises it.</summary>
    private static void ApplyBernoulli(PressureCell c, double speed, double dGameHours)
    {
        if (dGameHours <= 0) return;

        c.Strength += (float)(BernoulliStrength * BernoulliPerHour * (speed - 1.0) * dGameHours);
        if (c.Strength > MaxStrength) c.Strength = (float)MaxStrength;
        else if (c.Strength < -MaxStrength) c.Strength = -(float)MaxStrength;
    }

    private static void GrowStormLikelihood(PressureCell c, double dGameHours)
    {
        if (dGameHours <= 0) return;
        c.StormLikelihood = Math.Min(1f, c.StormLikelihood + (float)(StormLikelihoodGrowthPerHour * dGameHours));
    }

    /// <summary>Rotates <paramref name="baseAngle"/> toward the pole (warm) or equator (cold) side, scaled
    /// by <paramref name="airMassTemp"/> — shared by <see cref="Advect"/> (the cell's actual eased target
    /// velocity) and <see cref="Spawn"/> (so a cell aims upwind of the path it will actually tend to drift,
    /// not just the flat jetstream bearing). Mirrors how <see cref="WindField.SeasonalBiasZ"/> tilts the
    /// base jetstream angle rather than pushing a raw Z velocity component, so the bias stays meaningful
    /// relative to whatever the local flow is actually doing.</summary>
    private double AirMassBiasedAngle(double baseAngle, double z, float airMassTemp)
        => baseAngle + field.PolewardZSign(z) * airMassTemp * AirMassTempMoveBiasMaxAngle;

    /// <summary>Eases the ellipse's stored orientation toward the cell's own travel bearing, rather than
    /// snapping, so the shape rotates smoothly. Frozen (not chased) at near-zero own speed to avoid
    /// tracking a numerically noisy direction — common for slow/stalled systems, not a rare case.</summary>
    private static void EaseOrientation(PressureCell c, double velX, double velZ, double dGameHours)
    {
        if (dGameHours <= 0) return;
        double ownSpeed = Math.Sqrt(velX * velX + velZ * velZ);
        if (ownSpeed <= MinSpeedForOrientation) return;

        double target = OrientationFromVelocity(velX, velZ);
        double t = 1.0 - Math.Exp(-OrientationEaseRate * dGameHours);
        c.Orientation = (float)AngleMath.AngleLerp(c.Orientation, target, t);
    }

    /// <summary>
    /// Grows each low's <see cref="PressureCell.ColdFrontReach"/>/<see cref="PressureCell.WarmFrontReach"/>
    /// every hour (rate modulated by <see cref="DeltaTSlowdownScale"/> against whatever cell sits on that
    /// side), then checks whether the low's advancing cold front has physically CAUGHT another pressure
    /// cell (low or high) on its equatorward side — real occlusion is a cyclone's cold front overtaking
    /// the boundary ahead of it, a genuine two-cell event, not a fixed ratio against its own warm front or
    /// an age timer. Once caught (and only once — <see cref="PressureCell.OccludedUntilTotalHours"/> is a
    /// one-shot latch), the temperature difference between the catching low and the cell it caught decides
    /// the occlusion's character (<see cref="PressureCell.OcclusionKind"/>): the catching air colder than
    /// or equal to the caught cell's behaves like a cold front (kind = cold), warmer behaves like a warm
    /// front (kind = warm) — matching real synoptic classification. Storm likelihood then drops hard and
    /// remaining lifetime is cut short, same as before.
    /// </summary>
    /// <summary>Reach-growth multiplier against whichever cell sits on a side (see DeltaTSlowdownScale
    /// above): a similar air mass (<paramref name="targetIndex"/>'s <see cref="PressureCell.AirMassTemp"/>
    /// close to <paramref name="c"/>'s own) offers little resistance and the base rate applies almost
    /// unmodified; a starkly different one slows it. No cell on that side ⇒ unmodified base rate.</summary>
    private double ReachGrowthMult(PressureCell c, int targetIndex) => targetIndex >= 0
        ? 1.0 / (1.0 + DeltaTSlowdownScale * Math.Abs(c.AirMassTemp - field.Cells[targetIndex].AirMassTemp))
        : 1.0;

    private void ApplyOcclusion(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;
        lock (field.CellsLock)
        {
            for (int i = 0; i < field.Cells.Count; i++)
            {
                PressureCell c = field.Cells[i];
                if (!c.IsLow) continue; // highs don't occlude

                double poleSign = field.PolewardZSign(c.Z);
                int coldTarget = FindNearestCellOnSide(i, 0.0, -poleSign, OcclusionNeighbourMinStrength);
                int warmTarget = FindNearestCellOnSide(i, 0.0, poleSign, OcclusionNeighbourMinStrength);

                double coldMult = ReachGrowthMult(c, coldTarget);
                double warmMult = ReachGrowthMult(c, warmTarget);

                c.ColdFrontReach = (float)Math.Min(MaxFrontReach, c.ColdFrontReach + ColdReachGrowthPerHour * coldMult * dGameHours);
                c.WarmFrontReach = (float)Math.Min(MaxFrontReach, c.WarmFrontReach + WarmReachGrowthPerHour * warmMult * dGameHours);

                if (c.OccludedUntilTotalHours > 0) continue; // one-shot latch: already occluded once
                if (coldTarget < 0 || c.ColdFrontReach < OcclusionCatchMinReach) continue;

                PressureCell target = field.Cells[coldTarget];
                double dx = target.X - c.X, dz = target.Z - c.Z;
                double dist = Math.Sqrt(dx * dx + dz * dz);
                double catchDist = OcclusionCatchRadiusFactor * c.ColdFrontReach * c.Radius + OcclusionCatchTargetRadiusFactor * target.Radius;
                if (dist > catchDist) continue; // not caught yet

                c.OcclusionKind = c.AirMassTemp <= target.AirMassTemp ? (byte)1 : (byte)2; // 1 = cold, 2 = warm
                c.StormLikelihood = OcclusionAftermathStormLikelihood; // storm winds down once occluded
                c.OccludedUntilTotalHours = now + OccludedVisualHours;

                double remaining = c.DiesTotalHours - now;
                if (remaining > 0) c.DiesTotalHours = now + remaining * OcclusionDeathSpeedup;

                Dirty = true;
            }
        }
    }

    /// <summary>Finds the nearest OTHER significant pressure cell (any type) strictly on the
    /// (<paramref name="dirX"/>, <paramref name="dirZ"/>) side of cell <paramref name="selfIndex"/> —
    /// used both to pick a low's cold/warm-front reach-growth reference and its cold-front occlusion
    /// catch target. Positions are read directly from <c>field.Cells</c>, valid because this is only
    /// called from <see cref="ApplyOcclusion"/>, which runs after <see cref="Advect"/> has brought every
    /// cell's stored X/Z current.</summary>
    private int FindNearestCellOnSide(int selfIndex, double dirX, double dirZ, float minStrength)
    {
        PressureCell self = field.Cells[selfIndex];
        int best = -1;
        double bestDist = double.MaxValue;
        for (int j = 0; j < field.Cells.Count; j++)
        {
            if (j == selfIndex) continue;
            PressureCell o = field.Cells[j];
            if (Math.Abs(o.Strength) < minStrength) continue;
            double dx = o.X - self.X, dz = o.Z - self.Z;
            if (dx * dirX + dz * dirZ <= 0) continue; // not on this side
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < bestDist) { bestDist = dist; best = j; }
        }
        return best;
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
        if (dGameHours <= 0) return;
        lock (field.CellsLock)
        {
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

                c.Strength += (float)(WeatherPressureCoupling * forcing * dGameHours);
                if (c.Strength > MaxStrength) c.Strength = (float)MaxStrength;
                else if (c.Strength < -MaxStrength) c.Strength = -(float)MaxStrength;
            }
            Dirty = true;
        }
    }

    private void Expire(double now)
    {
        lock (field.CellsLock)
        {
            bool hasPlayers = HasPlayers; // no players online ⇒ nothing to cull distance against yet
            for (int i = field.Cells.Count - 1; i >= 0; i--)
            {
                PressureCell c = field.Cells[i];
                if (now >= c.DiesTotalHours || Math.Abs(c.Strength) < MinStrength
                    || (hasPlayers && IsTooFarFromAllPlayers(c)))
                {
                    field.Cells.RemoveAt(i);
                    Dirty = true;
                }
            }
        }
    }

    /// <summary>
    /// True if the nearest online player is farther than <see cref="DespawnMargin"/> PLUS the cell's own
    /// effective radius toward that player from the cell's centre — a system that's drifted this far off
    /// nobody's business isn't going to be seen or felt, so let it despawn rather than simulate it
    /// forever. The radius term means a large cell's edge (not just its centre) has to clear the margin
    /// before it despawns. Only called from <see cref="Expire"/> after a <see cref="HasPlayers"/> guard,
    /// so an empty player list never reaches here. Reads the lock-free <see cref="playerSnapshot"/> rather
    /// than <see cref="IWorldAccessor.AllOnlinePlayers"/> directly, since this runs on the dedicated sim
    /// thread and that collection can mutate concurrently as players join/leave on the main thread.
    /// </summary>
    private bool IsTooFarFromAllPlayers(PressureCell c)
    {
        foreach (PlayerPos p in playerSnapshot)
        {
            double dx = p.X - c.X;
            double dz = p.Z - c.Z;
            double distSq = dx * dx + dz * dz;
            double threshold = DespawnMargin + EllipseGeometry.RadiusToward(c, dx, dz);
            if (distSq <= threshold * threshold) return false;
        }
        return true;
    }

    /// <summary>
    /// Distance from (<paramref name="x"/>, <paramref name="z"/>) to the nearest online player, or
    /// <see cref="double.MaxValue"/> if nobody's online — used by <see cref="Spawn"/> to keep a new
    /// front from landing directly on top of a player. Reads the same <see cref="playerSnapshot"/> as
    /// <see cref="IsTooFarFromAllPlayers"/>, for the same off-main-thread safety reason.
    /// </summary>
    private double NearestOnlinePlayerDistance(double x, double z)
    {
        double best = double.MaxValue;
        foreach (PlayerPos p in playerSnapshot)
        {
            double dx = p.X - x, dz = p.Z - z;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < best) best = dist;
        }
        return best;
    }

    // Cap spawns/seeds per tick so a huge calendar jump (e.g. sleeping through several days) can't
    // flood the world in one tick, while still letting evolution speed up with world time.
    private const int MaxSpawnPerTick = 12;

    private void MaybeSpawn(double now, double dGameHours)
    {
        if (dGameHours <= 0 || !HasPlayers) return; // nobody to spawn near — nothing else to do this tick
        lock (field.CellsLock) { if (field.Cells.Count >= config.MaxActiveCells) return; }
        // Expected count scales with elapsed GAME time, so pressure systems evolve on the world clock —
        // and speed up while a player sleeps (the calendar fast-forwards → larger dGameHours per tick).
        SpawnExpected(now, LowSpawnRatePerDay / 24.0 * dGameHours, isLow: true);
        SpawnExpected(now, HighSpawnRatePerDay / 24.0 * dGameHours, isLow: false);
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
        lock (field.CellsLock)
        {
            if (field.Cells.Count >= config.MaxActiveCells) return;
            if (!TryGetSpawnAnchor(out double cx, out double cz)) return;

            // Full-range variety across the configured [CellRadiusMin, CellRadiusMax] span — synoptic
            // systems (unlike thermal seeds/heat-cold-waves below) span the whole configured size range.
            float radius = GameMath.Clamp(
                (float)(CellRadiusMin + rand.NextDouble() * (CellRadiusMax - CellRadiusMin)),
                CellRadiusMin, CellRadiusMax);

            // Mean jetstream bearing (seasonally tilted N/S). Systems spawn UPWIND of this bearing (± a
            // scatter spread) so they drift back down the jet THROUGH the player's area rather than being
            // carried off to the north/south when the stream tilts with the season — the old due-west lean
            // made seasonally-tilted systems sail past the player. Also pre-biased by this cell's own
            // (estimated) AirMassTemp lean — see AirMassBiasedAngle — since a systematically warm/cold cell
            // drifts consistently one way once its lean eases in during Advect, and aiming purely upwind of
            // the flat jet would otherwise under-account for that. The estimate is coarse (sampled at the
            // anchor, not the cell's actual spawn position below) — good enough to correct the systematic
            // lean; the cell's real, authoritative AirMassTemp is still computed at its true spawn position.
            double jetAng = field.SeasonalBiasMaxAngle * field.SeasonalBiasZ;
            float estimatedAirMassTemp = ComputeAirMassTemp(cx, cz);
            double travelAngle = AirMassBiasedAngle(jetAng, cz, estimatedAirMassTemp);
            double fx = Math.Cos(travelAngle), fz = Math.Sin(travelAngle); // downwind unit vector along the EXPECTED path

            // Advection speed in blocks/hour along the bearing, scaled by base strength.
            double advect = (300.0 + rand.NextDouble() * 300.0) * Math.Max(0.2, field.BaseStrength);
            double velX = fx * advect;
            double velZ = fz * advect;

            // Lifetime: lows are shorter-lived/sharper than highs. Raised well past the ~50-90h dispersal-
            // decay lifespan (see DispersalDecayPerHour) so decay — not this hard cap — is what actually
            // ends a cell's life; at the old 12-72h range this cap fired first, cutting cells off long
            // before they'd naturally fade, which (at the current, farther spawn distance) meant most never
            // survived long enough to advect within range of a player at all.
            double durationHours = isLow ? (100 + rand.NextDouble() * 100) : (150 + rand.NextDouble() * 150);

            double upwindAngle = travelAngle + Math.PI; // opposite the EXPECTED downwind bearing
            double bearing = upwindAngle + (rand.NextDouble() - 0.5) * 2.0 * SpawnAngleSpreadRad;
            double dirX = Math.Cos(bearing), dirZ = Math.Sin(bearing);

            double dist = SpawnDistanceBlocks + (rand.NextDouble() - 0.5) * 2.0 * SpawnDistanceJitter;
            double maxSafeDist = DespawnMargin + radius - SpawnDespawnSafetyMargin;
            if (dist > maxSafeDist) dist = maxSafeDist;

            double startX = cx + dirX * dist;
            double startZ = cz + dirZ * dist;

            // Don't spawn directly on top of ANY online player (the anchor is only one randomly-picked
            // player — the scatter above could still land near a different one) — push further upwind
            // instead of just discarding the attempt.
            double nearestPlayerDist = NearestOnlinePlayerDistance(startX, startZ);
            if (nearestPlayerDist < SpawnPlayerAvoidRadius)
            {
                double extra = SpawnPlayerAvoidRadius - nearestPlayerDist + SpawnPlayerAvoidPush;
                startX += dirX * extra;
                startZ += dirZ * extra;
            }

            // Highs are spawned as strong as lows: a high starts upwind and must travel (and slowly decay)
            // across the populated area before it can pair with a low into a front. Starting them weak (the
            // old -0.3..-1.0) left them below the marker/front threshold by the time they arrived, so the
            // overlay showed only lows and never drew a front line.
            // Widened from the old 0.6..1.6 (only a 2.67x weak-to-strong spread against the MaxStrength=3.0
            // clamp) — too narrow to make front wind visibly track pressure strength. 0.4..2.6 gives a 6.5x
            // spread so weak and intense systems actually feel different.
            float strength = isLow
                ? (float)(0.4 + rand.NextDouble() * 2.2)
                : (float)-(0.4 + rand.NextDouble() * 2.2);

            double minorRatio = SpawnMinorRatioMin + rand.NextDouble() * (SpawnMinorRatioMax - SpawnMinorRatioMin);

            field.Cells.Add(BuildCell(startX, startZ, strength, radius, velX, velZ, now, durationHours,
                (float)minorRatio, (float)rand.NextDouble(), ComputeAirMassTemp(startX, startZ)));
            // Re-anchor so the new cell's stored X/Z are valid at the shared anchor.
            field.AnchorHours = now;
            Dirty = true;
        }
    }

    /// <summary>Builds a newly-spawned cell's common fields.</summary>
    private static PressureCell BuildCell(double x, double z, float strength, float radius,
        double velX, double velZ, double now, double durationHours,
        float minorRatio, float stormLikelihood, float airMassTemp) => new()
    {
        X = x,
        Z = z,
        Strength = strength,
        Radius = radius,
        VelX = velX,
        VelZ = velZ,
        BornTotalHours = now,
        DiesTotalHours = now + durationHours,
        RadiusMinor = radius * minorRatio,
        Orientation = OrientationFromVelocity(velX, velZ),
        StormLikelihood = stormLikelihood,
        AirMassTemp = airMassTemp
    };

    /// <summary>World-space orientation angle (radians) of a cell's major/cross-stream axis derived from
    /// its velocity bearing — see <see cref="PressureCell.Orientation"/>.</summary>
    private static float OrientationFromVelocity(double velX, double velZ)
        => (float)(Math.Atan2(velZ, velX) + Math.PI / 2.0);


    /// <summary>
    /// Intrinsic air-mass character in [-1, 1] a cell is born with (see
    /// <see cref="PressureCell.AirMassTemp"/>): colder toward a real pole (via <see cref="WindField.LatitudeFunc"/>,
    /// a proxy for poleward birth), warmer/colder with the birth position's own season (via
    /// <see cref="IGameCalendar.GetSeasonRel"/>, which already accounts for hemisphere), plus random jitter
    /// so same-latitude, same-season systems still vary — real air masses aren't perfectly uniform.
    /// </summary>
    private float ComputeAirMassTemp(double x, double z)
    {
        double poleward = field.LatitudeFunc != null
            ? GameMath.Clamp((float)Math.Abs(field.LatitudeFunc(z)), 0f, 1f) // 0 = equator, 1 = pole
            : 0f; // latitude not ready yet — treat as neutral

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
    /// over time (and is identical to "the player" in singleplayer). Reads <see cref="playerSnapshot"/>
    /// rather than <see cref="IWorldAccessor.AllOnlinePlayers"/> directly — see its doc comment.
    /// </summary>
    private bool TryGetSpawnAnchor(out double x, out double z)
    {
        PlayerPos[] snap = playerSnapshot;
        if (snap.Length == 0) { x = 0; z = 0; return false; }

        PlayerPos p = snap[rand.Next(snap.Length)];
        x = p.X; z = p.Z;
        return true;
    }

    public WindFieldState Snapshot()
    {
        Dirty = false;
        lock (field.CellsLock)
        {
            return new WindFieldState
            {
                Cells = field.Cells.ToArray(),
                BaseStrength = field.BaseStrength,
                MeanderAmplitude = field.MeanderAmplitude,
                CyclonicInflowAngle = field.CyclonicInflowAngle,
                SeasonalBiasZ = field.SeasonalBiasZ,
                // Re-based to the shared anchor so the client's own extrapolation starts from the same
                // instant cell positions do (the drift carries its own clock server-side).
                DriftX = field.CloudDriftX + field.CloudDriftVelX * (field.AnchorHours - field.CloudDriftHours),
                DriftZ = field.CloudDriftZ + field.CloudDriftVelZ * (field.AnchorHours - field.CloudDriftHours),
                DriftVelX = field.CloudDriftVelX,
                DriftVelZ = field.CloudDriftVelZ,
                TotalHoursAnchor = field.AnchorHours
            };
        }
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
            lock (field.CellsLock)
            {
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

                // Cloud drift is a running integral, so it must survive a reload or the whole precipitation
                // texture would snap back to its origin on every world load. A pre-drift save leaves these
                // at protobuf's 0 default — i.e. no accumulated drift yet, which is exactly right.
                field.CloudDriftX = s.DriftX;
                field.CloudDriftZ = s.DriftZ;
                field.CloudDriftVelX = s.DriftVelX;
                field.CloudDriftVelZ = s.DriftVelZ;
                field.CloudDriftHours = s.TotalHoursAnchor;

                field.AnchorHours = s.TotalHoursAnchor; // provisional; re-anchored to the live clock next tick
                loadedPendingReanchor = true;           // defer time baseline to the first live tick (see OnTick)
                sapi.Logger.Notification("[dynamicwind] Loaded {0} pressure cells (saved anchor {1:0.0}h); re-anchoring to live clock.",
                    field.Cells.Count, field.AnchorHours);
            }
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[dynamicwind] Failed to load wind field, starting fresh: {0}", e);
        }
    }
}
