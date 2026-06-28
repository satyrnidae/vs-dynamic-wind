using System;
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

        Field.ThermalPressureFunc = (x, z, th) => TerrainThermal.PressureAt(api.World, x, z, th);

        sim = new WindFieldSimulation(api, Field, Config);

        api.Event.SaveGameLoaded += () => sim.Load();
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

    /// <summary>Slow over high terrain and deflect along height gradients (server-only; needs the sampler).</summary>
    protected override void ApplyOrographic(Vec3d pos, ref double nx, ref double nz, ref double strengthMul)
    {
        if (sampler == null || !Config.Orographic.Enabled) return;
        OrographicSampler.Apply(sampler, Config.Orographic, pos, ref nx, ref nz, ref strengthMul);
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
