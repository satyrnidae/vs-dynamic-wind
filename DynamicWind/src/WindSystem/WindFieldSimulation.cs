using System;
using DynamicWind.Config;
using DynamicWind.Network;
using DynamicWind.PressureSystem;
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
    private const double DespawnMargin = 30000.0; // a cell despawns once it's farther than this PLUS its own radius (toward the player) from EVERY online player

    // Velocity easing (AdvectWithWind): a cell doesn't snap straight to the local background wind each
    // tick, it exponentially eases its own velocity toward that target — real inertia, and what lets a
    // thermal-seeded cell (born stationary, see TrySeedOne) visibly speed up over the next several hours
    // as nearby cells' circulation and the base jetstream act on it, rather than jumping to full speed on
    // its very first tick.
    private const double VelocityEaseRatePerHour = 0.15;

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
    // clustered far to the west. Cell radii are a fraction of this range so neighbours overlap into
    // broad, merging blobs.
    private const double SpawnRangeBlocks = 6000.0; // scale reference for cell radii (see Spawn/TrySeedOne)

    // Spawn placement: a new front starts far out from a random player — like a real system approaching
    // from a distance — generally upwind so it drifts back through the player's area, scattered across a
    // spread of bearings rather than pinned to the exact upwind line. Distance is capped by the cell's own
    // radius so it never lands close enough to the DespawnMargin threshold to vanish the same tick it
    // spawns (see IsTooFarFromAllPlayers) — a bigger cell can safely spawn a little farther out.
    private const double SpawnDistanceBlocks = 8000.0;       // target distance from the anchor player
    private const double SpawnDistanceJitter = 1500.0;       // ± random spread on top of the target
    private const double SpawnDespawnSafetyMargin = 3000.0;  // stay this far inside the despawn threshold
    private const double SpawnAngleSpreadRad = Math.PI / 3.0; // ± scatter around the pure-upwind bearing
    private const double SpawnPlayerAvoidRadius = 2000.0;    // never spawn this close to ANY online player
    private const double SpawnPlayerAvoidPush = 500.0;       // extra clearance pushed past the avoid radius

    // Terrain thermal seeding: spawn mobile cells from terrain hotspots near players. Kept deliberately
    // modest — terrain pressure cell spawning was cluttering the field with far too many short-lived
    // cells at the old rate.
    private const double ThermalSeedRatePerDay = 4.0;    // seed attempts per game day
    private const double SeedScanRadius = 1500.0;        // sample box half-extent around population center
    private const double SeedThreshold = 0.25;           // minimum |thermal| to seed a cell
    private const double SeedGain = 1.2;                 // thermal magnitude → seeded cell strength
    private const double SeedMinSeparation = 600.0;      // don't seed atop an existing cell

    // Heat-wave/cold-wave events: a rare, very strong, near-stationary HIGH that traps ground air near
    // the surface — a real heat dome in warm months, an arctic-high cold snap in cold months (both are
    // anticyclonic highs; it's the trapped, subsiding air itself that reads hot or cold, not the pressure
    // sign). Sited on a genuine terrain-thermal hotspot like ordinary thermal seeding above, but the
    // heat/cold side is decided by the season at the spawn site, independent of the hotspot's own sign.
    // Deliberately far rarer than ordinary thermal seeding — this is meant to be a notable, occasional
    // event, not a routine one.
    private const double HeatColdWaveRatePerDay = 0.03;       // ~once every ~33 game-days on average
    private const double HeatColdWaveTempMin = 5.0;           // °C, "outside the average" floor
    private const double HeatColdWaveTempMax = 10.0;          // °C, ceiling
    private const float HeatColdWaveStrengthMin = 2.2f;       // magnitude; always a strong HIGH (negative)
    private const float HeatColdWaveStrengthMax = 2.8f;
    private const double HeatColdWaveDurationMinHours = 72.0;  // 3 days
    private const double HeatColdWaveDurationMaxHours = 120.0; // 5 days
    // Highs have a lot of inertia: an ordinary cell eases toward the background wind over ~a day
    // (VelocityEaseRatePerHour); a heat/cold-wave high eases far slower so it lingers over the same area
    // for its multi-day life instead of drifting off with the jet — only a strong, moving system nearby
    // (a big local background-wind term) will noticeably nudge it before then.
    private const double HeatColdWaveVelocityEaseRatePerHour = 0.02;

    // Synergy leaves the position index NearestPlayer relies on unpopulated, so its result can't be
    // trusted with it installed — fall back to the exhaustive AllOnlinePlayers entity scan instead.
    private const string SynergyModId = "synergy";

    private readonly ICoreServerAPI sapi;
    private readonly WindField field;
    private readonly DynamicWindConfig config;
    private readonly Random rand;
    private readonly bool synergyInstalled;

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
        synergyInstalled = sapi.ModLoader.IsModEnabled(SynergyModId);
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
        ApplyOcclusion(now, dGameHours);
        ApplyWeatherFeedback(now, dGameHours);
        Expire(now);
        MaybeSpawn(now, dGameHours);
        MaybeSeedThermal(now, dGameHours);
        MaybeSeedHeatColdWave(now, dGameHours);
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
        lock (field.CellsLock)
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
                        // Ease toward the local wind rather than snapping to it — see VelocityEaseRatePerHour.
                        // A heat/cold-wave high eases far slower (a lot of inertia — see HeatColdWaveVelocityEaseRatePerHour).
                        double targetVX = w.X * pd.AdvectionSpeed;
                        double targetVZ = w.Y * pd.AdvectionSpeed;
                        double easeRate = c.IsHeatColdWave ? HeatColdWaveVelocityEaseRatePerHour : VelocityEaseRatePerHour;
                        double ease = 1.0 - Math.Exp(-easeRate * dGameHours);
                        newVX[i] = c.VelX + (targetVX - c.VelX) * ease;
                        newVZ[i] = c.VelZ + (targetVZ - c.VelZ) * ease;
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

                ApplyDispersal(c, pd, dGameHours);
                ApplyBernoulli(c, pd, speed[i], dGameHours);
                GrowStormLikelihood(c, dGameHours);
                EaseOrientation(c, newVX[i], newVZ[i], dGameHours);
            }

            field.AnchorHours = now;
            Dirty = true;
        }
    }

    /// <summary>Radius/strength dispersal — see DispersalRate/DispersalSpreadPerHour/DispersalDecayPerHour.</summary>
    private static void ApplyDispersal(PressureCell c, DynamicWindConfig pd, double dGameHours)
    {
        if (pd.DispersalRate <= 0f || dGameHours <= 0) return;

        c.Radius += (float)(pd.DispersalRate * DispersalSpreadPerHour * dGameHours);
        if (c.Radius > MaxRadius) c.Radius = (float)MaxRadius;

        float minor = c.RadiusMinor > 1e-3f ? c.RadiusMinor : c.Radius;
        minor += (float)(pd.DispersalRate * DispersalSpreadPerHour * MinorDispersalFactor * dGameHours);
        c.RadiusMinor = Math.Min(minor, c.Radius); // never overtake the major axis

        c.Strength *= (float)Math.Exp(-pd.DispersalRate * DispersalDecayPerHour * dGameHours);
    }

    /// <summary>Faster-than-nominal flow lowers pressure (toward low, +); stagnant air raises it.</summary>
    private static void ApplyBernoulli(PressureCell c, DynamicWindConfig pd, double speed, double dGameHours)
    {
        if (pd.BernoulliStrength <= 0f || dGameHours <= 0) return;

        c.Strength += (float)(pd.BernoulliStrength * BernoulliPerHour * (speed - 1.0) * dGameHours);
        if (c.Strength > MaxStrength) c.Strength = (float)MaxStrength;
        else if (c.Strength < -MaxStrength) c.Strength = -(float)MaxStrength;
    }

    private static void GrowStormLikelihood(PressureCell c, double dGameHours)
    {
        if (dGameHours <= 0) return;
        c.StormLikelihood = Math.Min(1f, c.StormLikelihood + (float)(StormLikelihoodGrowthPerHour * dGameHours));
    }

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
        if (dGameHours <= 0 || config.WeatherPressureCoupling <= 0f) return;
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

                c.Strength += (float)(config.WeatherPressureCoupling * forcing * dGameHours);
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
            for (int i = field.Cells.Count - 1; i >= 0; i--)
            {
                PressureCell c = field.Cells[i];
                if (now >= c.DiesTotalHours || Math.Abs(c.Strength) < MinStrength || IsTooFarFromAllPlayers(c))
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
    /// before it despawns. No online players at all is NOT "too far" (nothing to cull against yet).
    /// Normally uses <see cref="IWorldAccessor.NearestPlayer"/> — a fast, thread-safe position-index
    /// lookup that avoids an entity scan — but Synergy leaves that index unpopulated, so with it
    /// installed we fall back to checking every online player's entity instead.
    /// </summary>
    private bool IsTooFarFromAllPlayers(PressureCell c)
    {
        if (synergyInstalled)
        {
            bool anyPlayer = false;
            foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
            {
                Entity e = plr.Entity;
                if (e == null) continue;
                anyPlayer = true;
                double dx = e.Pos.X - c.X;
                double dz = e.Pos.Z - c.Z;
                double distSq = dx * dx + dz * dz;
                double threshold = DespawnMargin + EllipseGeometry.RadiusToward(c, dx, dz);
                if (distSq <= threshold * threshold) return false;
            }
            return anyPlayer;
        }

        IPlayer? nearest = sapi.World.NearestPlayer(c.X, sapi.World.SeaLevel, c.Z);
        Entity? nearestEntity = nearest?.Entity;
        if (nearestEntity == null) return false;

        double ndx = nearestEntity.Pos.X - c.X;
        double ndz = nearestEntity.Pos.Z - c.Z;
        double nThreshold = DespawnMargin + EllipseGeometry.RadiusToward(c, ndx, ndz);
        return ndx * ndx + ndz * ndz > nThreshold * nThreshold;
    }

    /// <summary>
    /// Distance from (<paramref name="x"/>, <paramref name="z"/>) to the nearest online player, or
    /// <see cref="double.MaxValue"/> if nobody's online — used by <see cref="Spawn"/> to keep a new
    /// front from landing directly on top of a player. Same Synergy-aware fallback as
    /// <see cref="IsTooFarFromAllPlayers"/> (its position index goes unpopulated with Synergy installed).
    /// </summary>
    private double NearestOnlinePlayerDistance(double x, double z)
    {
        double best = double.MaxValue;
        if (synergyInstalled)
        {
            foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
            {
                Entity e = plr.Entity;
                if (e == null) continue;
                double dx = e.Pos.X - x, dz = e.Pos.Z - z;
                double dist = Math.Sqrt(dx * dx + dz * dz);
                if (dist < best) best = dist;
            }
            return best;
        }

        IPlayer? nearest = sapi.World.NearestPlayer(x, sapi.World.SeaLevel, z);
        Entity? nearestEntity = nearest?.Entity;
        if (nearestEntity == null) return best;
        double ndx = nearestEntity.Pos.X - x, ndz = nearestEntity.Pos.Z - z;
        return Math.Sqrt(ndx * ndx + ndz * ndz);
    }

    // Cap spawns/seeds per tick so a huge calendar jump (e.g. sleeping through several days) can't
    // flood the world in one tick, while still letting evolution speed up with world time.
    private const int MaxSpawnPerTick = 12;

    private void MaybeSpawn(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;
        lock (field.CellsLock) { if (field.Cells.Count >= config.MaxActiveCells) return; }
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
        lock (field.CellsLock)
        {
            if (field.Cells.Count >= config.MaxActiveCells) return;
            if (!TryGetSpawnAnchor(out double cx, out double cz)) return;

            // Size cells to a fraction of the spawn range so neighbours overlap into broad blobs that span
            // the visible map. Config radii (CellRadiusMin/Max, user-set) are the only clamp.
            float radius = GameMath.Clamp((float)((0.15 + rand.NextDouble() * 0.2) * SpawnRangeBlocks), config.CellRadiusMin, config.CellRadiusMax);

            // Mean jetstream bearing (seasonally tilted N/S). Systems spawn UPWIND of this bearing (± a
            // scatter spread) so they drift back down the jet THROUGH the player's area rather than being
            // carried off to the north/south when the stream tilts with the season — the old due-west lean
            // made seasonally-tilted systems sail past the player.
            double jetAng = field.SeasonalBiasMaxAngle * field.SeasonalBiasZ;
            double fx = Math.Cos(jetAng), fz = Math.Sin(jetAng); // downwind unit vector

            // Advection speed in blocks/hour along the bearing, scaled by base strength.
            double advect = (300.0 + rand.NextDouble() * 300.0) * Math.Max(0.2, config.BaseJetstreamStrength);
            double velX = fx * advect;
            double velZ = fz * advect;

            // Lifetime: lows are shorter-lived/sharper than highs.
            double durationHours = isLow ? (12 + rand.NextDouble() * 24) : (24 + rand.NextDouble() * 48);

            double upwindAngle = jetAng + Math.PI; // opposite the downwind jet bearing
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

    /// <summary>Builds a newly-spawned cell's common fields — shared by <see cref="Spawn"/>,
    /// <see cref="TrySeedOne"/>, and <see cref="TrySeedHeatColdWave"/>, which each add their own
    /// spawn-specific fields (e.g. heat/cold-wave flags) on top.</summary>
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
    /// Spawn a mobile, dispersing pressure cell from a terrain thermal hotspot (hot dry land → low,
    /// deep water → high). These then advect with the wind, so terrain influence is no longer pinned
    /// to the terrain — it drifts and spreads downwind. Rate-limited and kept clear of existing cells.
    /// </summary>
    private void MaybeSeedThermal(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;
        if (!config.ThermalSeeding || field.ThermalPressureFunc == null) return;
        lock (field.CellsLock) { if (field.Cells.Count >= config.MaxActiveCells) return; }

        // Expected seed attempts scale with elapsed game time (so seeding also speeds up during sleep).
        double expected = ThermalSeedRatePerDay / 24.0 * dGameHours;
        int attempts = (int)expected;
        if (attempts > MaxSpawnPerTick) attempts = MaxSpawnPerTick;
        if (rand.NextDouble() < expected - Math.Floor(expected)) attempts++;
        for (int i = 0; i < attempts; i++) TrySeedOne(now);
    }

    private void TrySeedOne(double now)
    {
        lock (field.CellsLock)
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
            double durationHours = 8 + rand.NextDouble() * 16;
            // Born stationary — a thermal hotspot itself isn't moving, so the cell it seeds shouldn't either.
            // With AdvectWithWind on, Advect's velocity easing (VelocityEaseRatePerHour) then lets it speed
            // up gradually as nearby cells' circulation and the base jetstream act on it, rather than jumping
            // to full jet speed on its very first tick.
            double minorRatio = SpawnMinorRatioMin + rand.NextDouble() * (SpawnMinorRatioMax - SpawnMinorRatioMin);

            field.Cells.Add(BuildCell(sx, sz, strength, radius, velX: 0.0, velZ: 0.0, now, durationHours,
                (float)minorRatio, (float)rand.NextDouble(), ComputeAirMassTemp(sx, sz)));
            field.AnchorHours = now;
            Dirty = true;
        }
    }

    /// <summary>
    /// Rare heat-wave/cold-wave event: spawn attempts scale with elapsed game time, same pattern as
    /// <see cref="MaybeSeedThermal"/>, but at a far lower rate — this is meant to be a notable, occasional
    /// event, not a routine one. Capped at one attempt per tick (never worth bursting several at once).
    /// </summary>
    private void MaybeSeedHeatColdWave(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;
        if (!config.HeatColdWaveEnabled || field.ThermalPressureFunc == null) return;
        lock (field.CellsLock) { if (field.Cells.Count >= config.MaxActiveCells) return; }

        double expected = HeatColdWaveRatePerDay / 24.0 * dGameHours;
        int attempts = expected >= 1.0 ? 1 : 0;
        if (rand.NextDouble() < expected - attempts) attempts++;
        for (int i = 0; i < attempts; i++) TrySeedHeatColdWave(now);
    }

    /// <summary>
    /// Spawn a heat-wave/cold-wave high: sited on a genuine terrain-thermal hotspot like ordinary thermal
    /// seeding (<see cref="TrySeedOne"/>), but always a strong HIGH regardless of the hotspot's own sign —
    /// a heat dome and an arctic-high cold snap are both anticyclonic. Warm-season vs. cold-season (and
    /// therefore heat wave vs. cold wave) is decided by the season at the spawn site, not by the hotspot.
    /// Spawned near-max strength and near-stationary (see HeatColdWaveVelocityEaseRatePerHour in Advect):
    /// that large initial magnitude is what gives it "a lot of inertia" — the same proportional dispersal
    /// decay every cell gets takes far longer to fade a near-max-strength cell below MinStrength, so it
    /// naturally lasts on the order of days rather than hours, with DiesTotalHours as a 3–5 day backstop.
    /// </summary>
    private void TrySeedHeatColdWave(double now)
    {
        lock (field.CellsLock)
        {
            if (field.Cells.Count >= config.MaxActiveCells) return;
            if (!TryGetSpawnAnchor(out double cx, out double cz)) return;

            double sx = cx + (rand.NextDouble() - 0.5) * 2.0 * SeedScanRadius;
            double sz = cz + (rand.NextDouble() - 0.5) * 2.0 * SeedScanRadius;
            double th = field.ThermalPressureFunc!(sx, sz, now);
            if (Math.Abs(th) < SeedThreshold) return; // needs a genuine terrain thermal condition to anchor to

            // Don't pile onto an existing cell (same guard as TrySeedOne).
            double elapsed = now - field.AnchorHours;
            foreach (PressureCell c in field.Cells)
            {
                double dx = (c.X + c.VelX * elapsed) - sx;
                double dz = (c.Z + c.VelZ * elapsed) - sz;
                if (dx * dx + dz * dz < SeedMinSeparation * SeedMinSeparation) return;
            }

            double seasonWarmth;
            try
            {
                var pos = new BlockPos((int)sx, sapi.World.SeaLevel, (int)sz, 0);
                float seasonRel = sapi.World.Calendar.GetSeasonRel(pos); // hemisphere-aware
                seasonWarmth = Math.Cos(2.0 * Math.PI * (seasonRel - SummerPeakRel)); // +1 midsummer, −1 midwinter
            }
            catch { return; } // season not ready yet — safer to skip this rare event than guess a side

            bool isHeatWave = seasonWarmth >= 0;
            double waveDelta = (HeatColdWaveTempMin + rand.NextDouble() * (HeatColdWaveTempMax - HeatColdWaveTempMin))
                * (isHeatWave ? 1.0 : -1.0);

            float radius = GameMath.Clamp((float)(0.15 * SpawnRangeBlocks), config.CellRadiusMin, config.CellRadiusMax);
            float strength = -(float)(HeatColdWaveStrengthMin + rand.NextDouble() * (HeatColdWaveStrengthMax - HeatColdWaveStrengthMin)); // always a strong HIGH
            double durationHours = HeatColdWaveDurationMinHours + rand.NextDouble() * (HeatColdWaveDurationMaxHours - HeatColdWaveDurationMinHours);
            double minorRatio = SpawnMinorRatioMin + rand.NextDouble() * (SpawnMinorRatioMax - SpawnMinorRatioMin);

            // highs never front/storm — StormLikelihood 0 is irrelevant, keep it inert.
            PressureCell cell = BuildCell(sx, sz, strength, radius, velX: 0.0, velZ: 0.0, now, durationHours,
                (float)minorRatio, stormLikelihood: 0f, airMassTemp: (float)(isHeatWave ? 1.0 : -1.0));
            cell.IsHeatColdWave = true;
            cell.WaveTemperatureDelta = (float)waveDelta;
            field.Cells.Add(cell);
            field.AnchorHours = now;
            Dirty = true;
            sapi.Logger.Notification("[dynamicwind] {0} forming near ({1:0}, {2:0}): {3:+0.0;-0.0}°C, ~{4:0.0} day(s).",
                isHeatWave ? "Heat wave" : "Cold wave", sx, sz, waveDelta, durationHours / 24.0);
        }
    }

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
        lock (field.CellsLock)
        {
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
