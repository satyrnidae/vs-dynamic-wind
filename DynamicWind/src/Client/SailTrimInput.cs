using System.Text;
using DynamicWind.Config;
using DynamicWind.Entities;
using DynamicWind.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace DynamicWind.Client;

/// <summary>
/// Hold-to-swing sail trim keybinds. The two hotkeys are registered rather than read raw so they show up
/// in the vanilla Controls GUI and can be rebound.
///
/// Vintage Story's hotkey handlers fire once on key DOWN, which cannot express "hold to keep swinging",
/// so the held state is polled on a client tick instead. What goes on the wire is INTENT (−1/0/+1),
/// emitted only when it changes — the server integrates it into the sail angle. That means one packet per
/// press and one per release rather than a stream, no drift if a packet is lost, and the sail stops by
/// itself if the player dismounts or disconnects mid-swing.
/// </summary>
internal sealed class SailTrimInput
{
    public const string HotkeyPort = "dynamicwindtrimport";
    public const string HotkeyStarboard = "dynamicwindtrimstarboard";

    private readonly ICoreClientAPI capi;
    private readonly IClientNetworkChannel channel;
    private long listenerId;
    private sbyte lastSentDir;
    private long lastSentBoatId;

    public SailTrimInput(ICoreClientAPI capi, IClientNetworkChannel channel)
    {
        this.capi = capi;
        this.channel = channel;
    }

    public void Register()
    {
        // Bracket keys, NOT comma/period: period opens chat to type a client command ("." prefix), so a
        // period binding is swallowed before it ever reaches a hotkey.
        capi.Input.RegisterHotKey(HotkeyPort, Lang.Get("dynamicwind:hotkey-trimport"),
            GlKeys.BracketLeft, HotkeyType.CharacterControls);
        capi.Input.RegisterHotKey(HotkeyStarboard, Lang.Get("dynamicwind:hotkey-trimstarboard"),
            GlKeys.BracketRight, HotkeyType.CharacterControls);

        listenerId = capi.Event.RegisterGameTickListener(OnClientTick, 33);

        // Client-side twin of `/dynamicwind sail`. The server command can only see the server's copy of
        // the state; rendering happens here, so a sail that reports a moving angle yet visibly does not
        // budge can only be diagnosed from this side. Invoked as ".dynamicwindsail".
        capi.ChatCommands.Create("dynamicwindsail")
            .WithDescription(Lang.Get("dynamicwind:command-sail-description"))
            .HandleWith(_ => ClientDiagnostics());
    }

    private TextCommandResult ClientDiagnostics()
    {
        var sb = new StringBuilder();
        DynamicWindConfig? cfg = DynamicWindRuntime.ClientConfig;
        sb.AppendLine(cfg == null
            ? "client config: NOT SYNCED yet"
            : $"client config: enabled={cfg.SailboatEnabled}, trim={cfg.SailboatSailAngleEnabled}");
        sb.AppendLine($"hotkeys: port='{Describe(HotkeyPort)}' starboard='{Describe(HotkeyStarboard)}', "
                      + $"held now: {capi.Input.IsHotKeyPressed(HotkeyPort)}/{capi.Input.IsHotKeyPressed(HotkeyStarboard)}");

        long id = ControlledSailboatId();
        sb.AppendLine($"controlled sailboat id: {(id == 0 ? "<none — sit in the controllable seat>" : id.ToString())}");

        Entity? boat = (capi.World?.Player?.Entity?.MountedOn as IMountableSeat)?.Entity;
        var trim = boat?.GetBehavior<EntityBehaviorSailTrim>();
        if (trim == null)
        {
            sb.Append("behaviour on client: NOT ATTACHED — the entity patch did not apply client-side.");
            return TextCommandResult.Success(sb.ToString());
        }

        sb.AppendLine($"behaviour: attached. sailPosition={boat!.WatchedAttributes.GetInt("sailPosition")}, "
                      + $"target={trim.TargetAngleDeg:0.0}°, current={trim.CurrentAngleDeg:0.0}°");
        sb.Append($"swing animation '{EntityBehaviorSailTrim.SwingAnimCode}' active: {trim.SwingAnimationActive}");
        return TextCommandResult.Success(sb.ToString());
    }

    private string Describe(string code)
        => capi.Input.GetHotKeyByCode(code)?.CurrentMapping?.ToString() ?? "<unbound>";

    public void Dispose()
    {
        if (listenerId != 0) capi.Event.UnregisterGameTickListener(listenerId);
        listenerId = 0;
    }

    private void OnClientTick(float dt)
    {
        DynamicWindConfig? cfg = DynamicWindRuntime.ClientConfig;
        long boatId = ControlledSailboatId();
        sbyte dir = 0;

        if (boatId != 0 && cfg != null && cfg.SailboatEnabled && cfg.SailboatSailAngleEnabled)
        {
            // IsHotKeyPressed honours the key's current (possibly rebound) mapping and its modifiers,
            // unlike a raw KeyboardKeyState probe.
            if (capi.Input.IsHotKeyPressed(HotkeyPort)) dir--;
            if (capi.Input.IsHotKeyPressed(HotkeyStarboard)) dir++;
        }

        // Also re-send on a boat change so a previous boat is never left mid-swing.
        if (dir == lastSentDir && boatId == lastSentBoatId) return;

        if (lastSentBoatId != 0 && lastSentBoatId != boatId && lastSentDir != 0)
        {
            channel.SendPacket(new SailTrimRequestPacket { BoatEntityId = lastSentBoatId, Dir = 0 });
        }
        if (boatId != 0)
        {
            channel.SendPacket(new SailTrimRequestPacket { BoatEntityId = boatId, Dir = dir });
        }

        lastSentDir = dir;
        lastSentBoatId = boatId;
    }

    /// <summary>Entity id of the sailboat this player is currently steering, or 0.</summary>
    private long ControlledSailboatId()
    {
        if (capi.World?.Player?.Entity?.MountedOn is not IMountableSeat seat) return 0;
        if (seat.Config?.Controllable != true) return 0;

        Entity? mount = seat.Entity;
        if (mount?.GetBehavior<EntityBehaviorSailTrim>() == null) return 0;
        return mount.EntityId;
    }
}
