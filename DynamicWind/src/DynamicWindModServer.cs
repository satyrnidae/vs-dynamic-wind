using System;
using System.Collections.Generic;
using AlgernonsTerrainSampler;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace DynamicWind;

/// <summary>
/// Server side: owns the pressure-system simulation, is the authoritative source of config + wind
/// field, syncs both to clients, applies the server-side Harmony patches (windmills, sailboats,
/// weather coupling), and applies orographic terrain correction via Algernons Terrain Sampler.
/// </summary>
public class DynamicWindModServer : DynamicWindModCommon
{
    private ICoreServerAPI sapi = null!;
    private IServerNetworkChannel ServerChannel => (IServerNetworkChannel)Channel;
    private WindFieldSimulation sim = null!;
    private TerrainSamplerMod? sampler;
    private Harmony? harmony;
    private long tickListenerId;
    private double lastBroadcastHours;
    private readonly List<FrontMarker> frontMarkerScratch = new();

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        DynamicWindRuntime.ServerField = Field;
        DynamicWindRuntime.ServerConfig = Config;

        sampler = api.ModLoader.GetModSystem<TerrainSamplerMod>();
        if (sampler == null)
        {
            Mod.Logger.Error("[dynamicwind] Algernons Terrain Sampler not found — orographic effects disabled. (It is a required dependency.)");
        }

        var thermalCache = new ThermalCache((x, z, th) => TerrainThermal.PressureAt(api.World, x, z, th));
        Field.ThermalPressureFunc = thermalCache.Get;
        if (sampler != null)
        {
            // Algernons Terrain Sampler gives terrain height at any coordinate without chunk gen.
            var heightCache = new ThermalCache((x, z, _) => sampler.GetBlockColumnHeight((int)x, (int)z), tile: 8.0, bucketHours: 1e9);
            Field.TerrainHeightFunc = (x, z) => heightCache.Get(x, z, 0);
        }

        sim = new WindFieldSimulation(api, Field, Config);

        api.Event.SaveGameLoaded += () =>
        {
            Field.SeaLevel = api.World.SeaLevel;
            Field.WorldHeight = api.WorldManager.MapSizeY;
            sim.Load();
        };
        api.Event.GameWorldSave += () => sim.Save();
        api.Event.PlayerJoin += OnPlayerJoin;
        api.Event.OnGetWindSpeed += OnGetWindSpeed;

        ServerChannel.SetMessageHandler<ConfigUpdateRequestPacket>(OnConfigUpdateRequest);

        tickListenerId = api.Event.RegisterGameTickListener(OnServerTick, 2000);

        harmony = new Harmony("dynamicwind.server");
        WindmillDirectionality.Apply(harmony, Mod.Logger);
        SailboatWind.Apply(harmony, api, Mod.Logger);
        WeatherCoupling.Init(api, Field, Config, Mod.Logger);

        DynamicWindCommands.Register(api, Field, Config);

        Mod.Logger.Notification("[dynamicwind] Server started.");
    }

    private void OnServerTick(float dt)
    {
        sim.OnTick(dt);
        double now = sapi.World.Calendar.TotalHours;

        // Recompute fronts from the current field and hand them to the weather coupling (new list each
        // tick, swapped atomically so OnGetClimate can read them lock-free off other threads).
        var newFronts = new List<FrontLine>();
        FrontComputer.Compute(Field, now, frontMarkerScratch, newFronts);
        WeatherCoupling.SetFronts(newFronts);

        bool heartbeat = now - lastBroadcastHours > 0.25; // ~ every 15 game-minutes
        if (sim.Dirty || heartbeat)
        {
            lastBroadcastHours = now;
            ServerChannel.BroadcastPacket(sim.Snapshot());
        }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        ServerChannel.SendPacket(Config, player);
        ServerChannel.SendPacket(sim.Snapshot(), player);
    }

    private void OnConfigUpdateRequest(IServerPlayer fromPlayer, ConfigUpdateRequestPacket packet)
    {
        if (!fromPlayer.HasPrivilege(Privilege.controlserver))
        {
            Mod.Logger.Notification("[dynamicwind] Rejected config change from non-privileged player {0}; reverting.", fromPlayer.PlayerName);
            ServerChannel.SendPacket(Config, fromPlayer); // revert their client
            return;
        }
        if (packet?.Config == null) return;

        Config = packet.Config;
        Config.Validate();
        ApplyConfigToField();
        DynamicWindRuntime.ServerConfig = Config;
        WeatherCoupling.UpdateConfig(Config);
        try { sapi.StoreModConfig(Config, ConfigFileName); } catch { /* logged elsewhere */ }

        ServerChannel.BroadcastPacket(Config);
        Mod.Logger.Notification("[dynamicwind] Config updated by {0} and broadcast to all clients.", fromPlayer.PlayerName);
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            sapi.Event.PlayerJoin -= OnPlayerJoin;
            sapi.Event.OnGetWindSpeed -= OnGetWindSpeed;
            if (tickListenerId != 0) sapi.Event.UnregisterGameTickListener(tickListenerId);
        }
        harmony?.UnpatchAll("dynamicwind.server");
        DynamicWindRuntime.ServerField = null;
        DynamicWindRuntime.ServerConfig = null;
    }
}
