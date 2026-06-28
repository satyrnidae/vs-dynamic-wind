using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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

        Advect(now);
        Expire(now);
        MaybeSpawn(now, dGameHours);
    }

    /// <summary>Move each cell to its current position and re-anchor to <paramref name="now"/>.</summary>
    private void Advect(double now)
    {
        double elapsed = now - field.AnchorHours;
        if (elapsed == 0) return;
        foreach (PressureCell c in field.Cells)
        {
            c.X += c.VelX * elapsed;
            c.Z += c.VelZ * elapsed;
        }
        field.AnchorHours = now;
        if (field.Cells.Count > 0) Dirty = true;
    }

    private void Expire(double now)
    {
        for (int i = field.Cells.Count - 1; i >= 0; i--)
        {
            if (now >= field.Cells[i].DiesTotalHours)
            {
                field.Cells.RemoveAt(i);
                Dirty = true;
            }
        }
    }

    private void MaybeSpawn(double now, double dGameHours)
    {
        if (dGameHours <= 0) return;
        double lowP = config.LowSpawnRatePerDay / 24.0 * dGameHours;
        double highP = config.HighSpawnRatePerDay / 24.0 * dGameHours;
        if (rand.NextDouble() < lowP) Spawn(now, isLow: true);
        if (rand.NextDouble() < highP) Spawn(now, isLow: false);
    }

    private void Spawn(double now, bool isLow)
    {
        if (!TryGetPopulationCenter(out double cx, out double cz)) return;

        float radius = config.CellRadiusMin + (float)rand.NextDouble() * (config.CellRadiusMax - config.CellRadiusMin);

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

        float strength = isLow
            ? (float)(0.5 + rand.NextDouble() * 1.0)
            : (float)-(0.3 + rand.NextDouble() * 0.7);

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
