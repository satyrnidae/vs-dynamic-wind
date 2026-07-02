using DynamicWind.Client;
using DynamicWind.Client.MapLayers;
using DynamicWind.Client.Patches;
using DynamicWind.Config;
using DynamicWind.Network;
using DynamicWind.Patches;
using DynamicWind.ThermalPressure;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace DynamicWind;

/// <summary>
/// Client side: mirrors the server's config + wind field, runs the directional wind hook (so local
/// particles/foliage/dropped items follow the wind), patches the cloud renderer so clouds drift along
/// the wind vector, and wires the optional ConfigLib GUI.
/// </summary>
public class DynamicWindModClient : DynamicWindModCommon
{
    private ICoreClientAPI capi = null!;
    private IClientNetworkChannel ClientChannel => (IClientNetworkChannel)Channel;
    private Harmony? harmony;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        DynamicWindRuntime.ClientField = Field;
        DynamicWindRuntime.ClientConfig = Config;
        DynamicWindRuntime.ClientModSystem = this;
        api.Event.LevelFinalize += () =>
        {
            Field.SeaLevel = api.World.SeaLevel;
            Field.WorldHeight = api.World.BlockAccessor.MapSizeY;
            Field.MapCenterZ = api.World.BlockAccessor.MapSizeZ / 2.0;
        };

        SetupThermalPressureFunc(api);
        // Client has no terrain sampler; use the rain-map height (loaded chunks) so the minimap shows
        // orographic curving around terrain near the player.
        var heightCache = new ThermalCache((x, z, _) => api.World.BlockAccessor.GetRainMapHeightAt((int)x, (int)z), tile: 8.0, bucketHours: 1e9);
        Field.TerrainHeightFunc = (x, z) => heightCache.Get(x, z, 0);

        api.Event.OnGetWindSpeed += OnGetWindSpeed;
        api.Event.OnGetClimate += OnGetClimate;

        ClientChannel.SetMessageHandler<DynamicWindConfig>(OnConfigReceived);
        ClientChannel.SetMessageHandler<WindFieldState>(OnStateReceived);

        harmony = new Harmony("dynamicwind.client");
        CloudRendererPatch.Apply(harmony, api, Mod.Logger);
        // Gate precipitation by pressure on the client too, so the Doppler radar (which reads the
        // client's GetPrecipitation) matches the clouds/rain. Idempotent: in singleplayer the server
        // already patched the shared base method; the static guard makes this a no-op there.
        WeatherCoupling.Apply(harmony, Mod.Logger);

        // Optional: fix the locally-installed HUD Clock mod's wind readout (it reads vanilla's raw
        // scalar wind, bypassing our directional hook) and add an 8-point cardinal direction.
        if (api.ModLoader.IsModEnabled(HudClockWindPatch.HudClockModId))
        {
            HudClockWindPatch.Apply(harmony, api, Mod.Logger);
        }

        // Wind-direction arrow overlay on the minimap + world map. Registered before the map manager
        // instantiates layers (OnLvlFinalize), so it is picked up automatically.
        WorldMapManager? mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
        mapManager?.RegisterMapLayer<WindArrowMapLayer>("dynamicwindarrows", 1.0);
        mapManager?.RegisterMapLayer<DopplerRadarMapLayer>("dynamicwindradar", 0.9);
        mapManager?.RegisterMapLayer<FrontMapLayer>("dynamicwindfronts", 0.95);

        Mod.Logger.Notification("[dynamicwind] Client started.");
    }

    public override void AssetsLoaded(ICoreAPI api)
    {
        base.AssetsLoaded(api);

        // Optional ConfigLib GUI. Register HERE, not in StartClientSide: ConfigLib snapshots its mod
        // list once, when it builds the config window at world-join config sync. AssetsLoaded is the
        // one phase that runs both AFTER lang assets load (so setting labels localize) and BEFORE that
        // snapshot — registering any later (e.g. StartClientSide) leaves us off the list entirely.
        // Isolated + guarded so a missing configlib.dll can never trip a type-load failure (see
        // DynamicWindConfigLib).
        if (api.ModLoader.IsModEnabled(DynamicWindConfigLib.ConfigLibModId))
        {
            DynamicWindConfigLib.TryRegister(api, Config, OnConfigEditedLocally, ApplyConfigToField, Mod.Logger);
        }
    }

    private void OnConfigReceived(DynamicWindConfig packet)
    {
        Config.CopyFrom(packet);
        Config.Validate();
        ApplyConfigToField();
    }

    private void OnStateReceived(WindFieldState s)
    {
        if (s?.Cells == null) return;
        Field.Cells.Clear();
        Field.Cells.AddRange(s.Cells);
        Field.BaseStrength = s.BaseStrength;
        Field.MeanderAmplitude = s.MeanderAmplitude;
        Field.CyclonicInflowAngle = s.CyclonicInflowAngle;
        Field.SeasonalBiasZ = s.SeasonalBiasZ;
        Field.AnchorHours = s.TotalHoursAnchor;
    }

    /// <summary>
    /// Called when the player edits config in the ConfigLib GUI. Config is server-authoritative, so we
    /// don't apply locally; we ask the server (it checks privilege, applies, and broadcasts back). In
    /// singleplayer the host is privileged so this round-trips immediately.
    /// </summary>
    private void OnConfigEditedLocally()
    {
        ClientChannel.SendPacket(new ConfigUpdateRequestPacket { Config = Config });
    }

    public override void Dispose()
    {
        if (capi != null)
        {
            capi.Event.OnGetWindSpeed -= OnGetWindSpeed;
            capi.Event.OnGetClimate -= OnGetClimate;
        }
        harmony?.UnpatchAll("dynamicwind.client");
        DynamicWindRuntime.ClientField = null;
        DynamicWindRuntime.ClientConfig = null;
        DynamicWindRuntime.ClientModSystem = null;
    }
}
