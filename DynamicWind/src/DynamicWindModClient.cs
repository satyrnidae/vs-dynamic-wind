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

        Field.ThermalPressureFunc = (x, z, th) => TerrainThermal.PressureAt(api.World, x, z, th);

        api.Event.OnGetWindSpeed += OnGetWindSpeed;

        ClientChannel.SetMessageHandler<DynamicWindConfig>(OnConfigReceived);
        ClientChannel.SetMessageHandler<WindFieldState>(OnStateReceived);

        harmony = new Harmony("dynamicwind.client");
        CloudRendererPatch.Apply(harmony, api, Mod.Logger);

        // Wind-direction arrow overlay on the minimap + world map. Registered before the map manager
        // instantiates layers (OnLvlFinalize), so it is picked up automatically.
        api.ModLoader.GetModSystem<WorldMapManager>()?.RegisterMapLayer<WindArrowMapLayer>("dynamicwindarrows", 1.0);

        // Optional ConfigLib GUI. Isolated + guarded so a missing configlib.dll can never trip a
        // type-load failure (see DynamicWindConfigLib).
        if (api.ModLoader.IsModEnabled(DynamicWindConfigLib.ConfigLibModId))
        {
            DynamicWindConfigLib.TryRegister(api, Config, OnConfigEditedLocally, ApplyConfigToField, Mod.Logger);
        }

        Mod.Logger.Notification("[dynamicwind] Client started.");
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
        }
        harmony?.UnpatchAll("dynamicwind.client");
        DynamicWindRuntime.ClientField = null;
        DynamicWindRuntime.ClientConfig = null;
    }
}
