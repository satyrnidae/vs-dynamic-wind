using System.Text;
using DynamicWind.Client.Gui;
using DynamicWind.Config;
using DynamicWind.Entities;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace DynamicWind.Client;

/// <summary>
/// Client-side sail trim diagnostics (<c>.dwind sail</c>). Trim input itself now comes from
/// <c>SailboatWind.HandleCtrlTrim</c> (Ctrl+A/D repurposing the tiller's own turn keys, read directly off
/// synced <c>EntityControls</c> in the SeatsToMotion postfix) - the earlier hold-to-swing bracket-key
/// hotkeys and their packet path were removed once that alternate control was confirmed working, since
/// having two independent input paths write the same server-side trim state was only useful during that
/// verification window.
/// </summary>
internal sealed class SailTrimInput
{
    private readonly ICoreClientAPI capi;
    private GuiDialogSailGraph? sailGraph;

    public SailTrimInput(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Register()
    {
        // Client-side command tree, bundled under the same "dwind" root as the server's `/dwind` (see
        // DynamicWindCommands) but invoked with "." since these are client-only (no round trip) - the
        // client and server chat command registries are entirely separate, so the shared root name is
        // just for parity, not a literal shared namespace.
        capi.ChatCommands.Create("dwind")
            .WithDescription(Lang.Get("dynamicwind:command-description"))
            // Client-side twin of `/dwind sail`. The server command can only see the server's copy of the
            // state; rendering happens here, so a sail that reports a moving angle yet visibly does not
            // budge can only be diagnosed from this side. Invoked as ".dwind sail".
            .BeginSubCommand("sail")
                .WithDescription(Lang.Get("dynamicwind:command-sail-description"))
                .HandleWith(_ => ClientDiagnostics())
            .EndSubCommand()
            // Live Cairo-drawn plot of the trim curve - see GuiDialogSailGraph. Built lazily on first
            // toggle (not in the constructor) since most players will never open it.
            .BeginSubCommand("sailgraph")
                .WithDescription(Lang.Get("dynamicwind:command-sailgraph-description"))
                .HandleWith(_ =>
                {
                    sailGraph ??= new GuiDialogSailGraph(capi);
                    sailGraph.Toggle();
                    return TextCommandResult.Success();
                })
            .EndSubCommand();
    }

    private TextCommandResult ClientDiagnostics()
    {
        var sb = new StringBuilder();
        DynamicWindConfig? cfg = DynamicWindRuntime.ClientConfig;
        sb.AppendLine(cfg == null
            ? "client config: NOT SYNCED yet"
            : $"client config: enabled={cfg.SailboatEnabled}, trim={cfg.SailboatSailAngleEnabled}");

        Entity? boat = (capi.World?.Player?.Entity?.MountedOn as IMountableSeat)?.Entity;
        var trim = boat?.GetBehavior<EntityBehaviorSailTrim>();
        if (trim == null)
        {
            sb.Append("behaviour on client: NOT ATTACHED - the entity patch did not apply client-side.");
            return TextCommandResult.Success(sb.ToString());
        }

        sb.AppendLine($"behaviour: attached. sailPosition={boat!.WatchedAttributes.GetInt("sailPosition")}, "
                      + $"target={trim.TargetAngleDeg:0.0}°, current={trim.CurrentAngleDeg:0.0}°");
        sb.Append($"swing animation '{EntityBehaviorSailTrim.SwingAnimCode}' active: {trim.SwingAnimationActive}");
        return TextCommandResult.Success(sb.ToString());
    }
}
