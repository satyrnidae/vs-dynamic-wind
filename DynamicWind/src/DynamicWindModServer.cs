using AlgernonsTerrainSampler;
using DynamicWind.Compat;
using DynamicWind.Entities;
using DynamicWind.Network;
using DynamicWind.Patches;
using DynamicWind.TerrainSystem;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

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

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        DynamicWindRuntime.ServerField = Field;
        DynamicWindRuntime.ServerConfig = Config;

#pragma warning disable CA1416 // Algernons Terrain Sampler is a hard dependency (see modinfo.json), not actually Windows-only
        sampler = api.ModLoader.GetModSystem<TerrainSamplerMod>();
        if (sampler == null)
        {
            Mod.Logger.Error("[dynamicwind] Algernons Terrain Sampler not found — orographic effects disabled. (It is a required dependency.)");
        }

        SetupTerrainTempFunc(api);
        SetupTerrainHumidityFunc(api);
        if (sampler != null)
        {
            // Algernons Terrain Sampler gives terrain height at any coordinate without chunk gen.
            var heightCache = new ThermalCache((x, z, _) => sampler.GetBlockColumnHeight((int)x, (int)z), tile: 8.0, bucketHours: 1e9);
            Field.TerrainHeightFunc = (x, z) => heightCache.Get(x, z, 0);
        }
#pragma warning restore CA1416

        sim = new WindFieldSimulation(api, Field, Config);

        api.Event.SaveGameLoaded += () =>
        {
            SetupWorldGeometry(api);
            sim.Load();
            // Prime the player snapshot before the sim thread's very first tick so it doesn't run
            // despawn/spawn logic against an empty player list.
            sim.CapturePlayerSnapshot();
            sim.Start();
        };
        api.Event.GameWorldSave += () => sim.Save();
        api.Event.PlayerJoin += OnPlayerJoin;
        api.Event.OnGetWindSpeed += OnGetWindSpeed;
        api.Event.OnGetClimate += OnGetClimate;

        ServerChannel.SetMessageHandler<ConfigUpdateRequestPacket>(OnConfigUpdateRequest);
        ServerChannel.SetMessageHandler<SailTrimRequestPacket>(OnSailTrimRequest);

        tickListenerId = api.Event.RegisterGameTickListener(OnServerTick, 2000);

        harmony = new Harmony("dynamicwind.server");
        WindmillDirectionality.Apply(harmony, Mod.Logger);
        SailboatWind.Apply(harmony, Mod.Logger);
        WeatherCoupling.Apply(harmony, Mod.Logger);

        // Optional: Immersive Body Temperature's humidity/wetness compat, only entered if that mod is
        // loaded (soft dependency, no modinfo entry — resolved entirely via AccessTools).
        if (api.ModLoader.IsModEnabled(ImmersiveBodyTemperatureCompat.ModId) && Config.HumidityBodyTempCompatEnabled)
        {
            ImmersiveBodyTemperatureCompat.Apply(harmony, Mod.Logger);
        }

        DynamicWindCommands.Register(api, Field, Config);

        Mod.Logger.Notification("[dynamicwind] Server started.");
    }

    // The actual simulation now runs on its own dedicated thread (see WindFieldSimulation.Start); this
    // main-thread tick only refreshes the player-position snapshot the sim thread reads (main-thread-only
    // safe, since AllOnlinePlayers can mutate concurrently with player join/leave) and broadcasts the
    // synced state to clients.
    private void OnServerTick(float dt)
    {
        sim.CapturePlayerSnapshot();
        double now = sapi.World.Calendar.TotalHours;

        // Fronts are a pure, deterministic function of the synced pressure cells, so the client computes
        // them itself for the overlay; precipitation is coupled directly at GetRainCloudness (see
        // WeatherCoupling), so the server no longer needs to compute fronts here.
        bool heartbeat = now - lastBroadcastHours > 0.25; // ~ every 15 game-minutes
        if (sim.Dirty || heartbeat)
        {
            lastBroadcastHours = now;
            ServerChannel.BroadcastPacket(sim.Snapshot());
        }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        // Order matters: OnStateReceived's BaseStrength/MeanderAmplitude/etc. overwrite whatever
        // ApplyConfigToField set from OnConfigReceived, so Config must arrive first. The channel
        // delivers packets in send order, so sending in this order is sufficient — don't reorder.
        ServerChannel.SendPacket(Config, player);
        ServerChannel.SendPacket(sim.Snapshot(), player);
    }

    /// <summary>
    /// Applies a client's held sail-trim direction. The client only ever states intent (−1/0/+1); the
    /// server owns the angle and integrates it, so a lost or duplicated packet cannot accumulate drift.
    /// Only a player actually sitting in a controllable seat of that boat may steer its sail.
    /// </summary>
    private void OnSailTrimRequest(IServerPlayer fromPlayer, SailTrimRequestPacket packet)
    {
        if (packet == null || !Config.SailboatEnabled || !Config.SailboatSailAngleEnabled) return;

        Entity? boat = sapi.World.GetEntityById(packet.BoatEntityId);
        EntityBehaviorSailTrim? trim = boat?.GetBehavior<EntityBehaviorSailTrim>();
        if (trim == null) return;

        EntityBehaviorSeatable? seatable = boat!.GetBehavior<EntityBehaviorSeatable>();
        if (seatable?.Seats == null) return;

        bool controlling = false;
        foreach (IMountableSeat seat in seatable.Seats)
        {
            if (seat?.Passenger == fromPlayer.Entity && seat.Config?.Controllable == true) { controlling = true; break; }
        }
        if (!controlling) return;

        trim.SetTrimIntent(packet.Dir);
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

        Config.CopyFrom(packet.Config);
        Config.Validate();
        ApplyConfigToField();
        DynamicWindRuntime.ServerConfig = Config;
        try { sapi.StoreModConfig(Config, ConfigFileName); } catch { /* logged elsewhere */ }

        ServerChannel.BroadcastPacket(Config);
        Mod.Logger.Notification("[dynamicwind] Config updated by {0} and broadcast to all clients.", fromPlayer.PlayerName);
    }

    public override void Dispose()
    {
        sim?.Stop();
        if (sapi != null)
        {
            sapi.Event.PlayerJoin -= OnPlayerJoin;
            sapi.Event.OnGetWindSpeed -= OnGetWindSpeed;
            sapi.Event.OnGetClimate -= OnGetClimate;
            if (tickListenerId != 0) sapi.Event.UnregisterGameTickListener(tickListenerId);
        }
        harmony?.UnpatchAll("dynamicwind.server");
        DynamicWindRuntime.ServerField = null;
        DynamicWindRuntime.ServerConfig = null;
    }
}
