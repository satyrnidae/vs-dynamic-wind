using System;
using System.Linq;
using System.Text;
using DynamicWind.Config;
using DynamicWind.Entities;
using DynamicWind.PressureSystem;
using DynamicWind.WindSystem;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace DynamicWind;

/// <summary>Server debug/inspection commands: <c>/dynamicwind debug</c>, <c>/dynamicwind cells</c>.</summary>
internal static class DynamicWindCommands
{
    public static void Register(ICoreServerAPI api, WindField field, DynamicWindConfig config)
    {
        api.ChatCommands.Create("dynamicwind")
            .WithDescription(Lang.Get("dynamicwind:command-description"))
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("debug")
                .WithDescription(Lang.Get("dynamicwind:command-debug-description"))
                .HandleWith(args => Debug(args, api, field, config))
            .EndSubCommand()
            .BeginSubCommand("cells")
                .WithDescription(Lang.Get("dynamicwind:command-cells-description"))
                .HandleWith(args => Cells(args, api, field))
            .EndSubCommand()
            .BeginSubCommand("sail")
                .WithDescription(Lang.Get("dynamicwind:command-sail-description"))
                .HandleWith(args => Sail(args, api, config))
            .EndSubCommand();
    }

    private static TextCommandResult Debug(TextCommandCallingArgs args, ICoreServerAPI api, WindField field, DynamicWindConfig config)
    {
        Entity? e = args.Caller.Entity;
        if (e == null) return TextCommandResult.Error(Lang.Get("dynamicwind:command-error-not-player"));

        double th = api.World.Calendar.TotalHours;
        Vec2d w = field.WindAt(e.Pos.X, e.Pos.Z, th);
        double mag = w.Length();
        double bearing = Math.Atan2(w.Y, w.X) * GameMath.RAD2DEG; // 0 = East(+X), 90 = South(+Z)
        double pressure = field.PressureAt(e.Pos.X, e.Pos.Y, e.Pos.Z, th);

        var sb = new StringBuilder();
        sb.AppendLine(Lang.Get("dynamicwind:command-debug-bearing", $"{bearing:0.0}"));
        sb.AppendLine(Lang.Get("dynamicwind:command-debug-strength", $"{mag:0.000}"));
        string pressureDesc = Lang.Get(pressure > 0 ? "dynamicwind:command-pressure-low" : pressure < 0 ? "dynamicwind:command-pressure-high" : "dynamicwind:command-pressure-neutral");
        sb.AppendLine(Lang.Get("dynamicwind:command-debug-pressure", $"{pressure:0.000}", pressureDesc));
        string biasDesc = Lang.Get(field.SeasonalBiasZ < -0.05 ? "dynamicwind:command-bias-sw" : field.SeasonalBiasZ > 0.05 ? "dynamicwind:command-bias-nw" : "dynamicwind:command-bias-w");
        sb.AppendLine(Lang.Get("dynamicwind:command-debug-bias", $"{field.SeasonalBiasZ:0.00}", biasDesc));
        double humidity = field.HumidityAt(e.Pos.X, e.Pos.Z, th);
        sb.AppendLine(Lang.Get("dynamicwind:command-debug-humidity", $"{humidity * 100:0}"));
        lock (field.CellsLock) { sb.Append(Lang.Get("dynamicwind:command-debug-active-cells", field.Cells.Count)); }
        return TextCommandResult.Success(sb.ToString());
    }

    /// <summary>
    /// Reports the whole sail-trim chain for the boat the caller is sitting on, so an inert control can
    /// be traced to a specific link rather than guessed at: is the behaviour even attached (i.e. did the
    /// entity patch apply), is the baked <c>sailtrim</c> swing animation active (i.e. did the shape patch
    /// apply), and is the angle actually moving when a key is held. Server-side state only — the client
    /// runs its own copy of the animation for rendering.
    /// </summary>
    private static TextCommandResult Sail(TextCommandCallingArgs args, ICoreServerAPI api, DynamicWindConfig config)
    {
        Entity? caller = args.Caller.Entity;
        if (caller is not EntityPlayer player) return TextCommandResult.Error(Lang.Get("dynamicwind:command-error-not-player"));

        var sb = new StringBuilder();
        sb.AppendLine($"config: SailboatEnabled={config.SailboatEnabled}, SailAngleEnabled={config.SailboatSailAngleEnabled}, "
                      + $"maxTrim={config.SailboatMaxTrimAngleDeg:0}°, swingRate={config.SailboatSwingRateDegPerSec:0}°/s, step={config.SailboatTrimStepDeg:0}°");

        IMountableSeat? seat = player.MountedOn;
        if (seat == null)
        {
            sb.AppendLine("mounted: NO — sit in the boat's controllable seat, then run this again.");
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        Entity? boat = seat.Entity;
        sb.AppendLine($"mounted: yes, on {boat?.Code} (id {boat?.EntityId}), seat controllable={seat.Config?.Controllable == true}");
        if (boat == null) return TextCommandResult.Success(sb.ToString().TrimEnd());

        var trim = boat.GetBehavior<EntityBehaviorSailTrim>();
        if (trim == null)
        {
            sb.AppendLine("behaviour 'dynamicwindsailtrim': NOT ATTACHED — the entity patch "
                          + "(boat-sailed-sailtrim.json) did not apply, or the behaviour class failed to register. "
                          + "Nothing will respond until this is fixed.");
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        sb.AppendLine($"behaviour: attached. sailPosition={boat.WatchedAttributes.GetInt("sailPosition")}, "
                      + $"target={trim.TargetAngleDeg:0.0}°, current={trim.CurrentAngleDeg:0.0}°, intent={trim.TrimIntent:+0;-0;0}");
        sb.AppendLine(trim.SwingAnimationActive
            ? $"swing animation: active (server copy of '{EntityBehaviorSailTrim.SwingAnimCode}')"
            : $"swing animation: NOT ACTIVE — could not find/start '{EntityBehaviorSailTrim.SwingAnimCode}'. "
              + "The shape patch (boat-sailed-shape.json) may not have applied.");

        var boxes = boat.GetBehavior<EntityBehaviorSelectionBoxes>();
        string codes = boxes?.selectionBoxes == null
            ? "<none>"
            : string.Join(", ", boxes.selectionBoxes
                .Select(b => b.AttachPoint?.Code ?? "?")
                .Where(c => c.StartsWith("Sheet", StringComparison.Ordinal)));
        sb.AppendLine($"sheet selection boxes: {(codes.Length == 0 ? "<none resolved>" : codes)}");
        return TextCommandResult.Success(sb.ToString().TrimEnd());
    }

    private static TextCommandResult Cells(TextCommandCallingArgs args, ICoreServerAPI api, WindField field)
    {
        double th = api.World.Calendar.TotalHours;
        var sb = new StringBuilder();
        lock (field.CellsLock)
        {
            if (field.Cells.Count == 0) return TextCommandResult.Success(Lang.Get("dynamicwind:command-cells-none"));
            double elapsed = th - field.AnchorHours;
            sb.AppendLine(Lang.Get("dynamicwind:command-cells-header", field.Cells.Count));
            foreach (PressureCell c in field.Cells)
            {
                double cx = c.X + c.VelX * elapsed;
                double cz = c.Z + c.VelZ * elapsed;
                string typeDesc = Lang.Get(c.IsLow ? "dynamicwind:command-cell-low" : "dynamicwind:command-cell-high");
                sb.AppendLine(Lang.Get("dynamicwind:command-cells-row", typeDesc, $"{c.Strength:0.00}", $"{c.Radius:0}", $"{c.RadiusMinor:0}", $"{cx:0}", $"{cz:0}", $"{Math.Max(0, c.DiesTotalHours - th):0.0}", $"{c.AirMassTemp:+0.00;-0.00}"));
            }
        }
        return TextCommandResult.Success(sb.ToString().TrimEnd());
    }
}
