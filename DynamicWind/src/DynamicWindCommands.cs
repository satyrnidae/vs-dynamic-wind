using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace DynamicWind;

/// <summary>Server debug/inspection commands: <c>/dynamicwind debug</c>, <c>/dynamicwind cells</c>.</summary>
internal static class DynamicWindCommands
{
    public static void Register(ICoreServerAPI api, WindField field, DynamicWindConfig config)
    {
        api.ChatCommands.Create("dynamicwind")
            .WithDescription("Inspect the dynamic wind field")
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("debug")
                .WithDescription("Print wind direction, speed multiplier and pressure at your position")
                .HandleWith(args => Debug(args, api, field, config))
            .EndSubCommand()
            .BeginSubCommand("cells")
                .WithDescription("List active pressure systems")
                .HandleWith(args => Cells(args, api, field))
            .EndSubCommand();
    }

    private static TextCommandResult Debug(TextCommandCallingArgs args, ICoreServerAPI api, WindField field, DynamicWindConfig config)
    {
        Entity? e = args.Caller.Entity;
        if (e == null) return TextCommandResult.Error("Run this as a player.");

        double th = api.World.Calendar.TotalHours;
        Vec2d w = field.WindAt(e.Pos.X, e.Pos.Z, th);
        double mag = w.Length();
        double bearing = Math.Atan2(w.Y, w.X) * GameMath.RAD2DEG; // 0 = East(+X), 90 = South(+Z)
        double pressure = field.PressureAt(e.Pos.X, e.Pos.Y, e.Pos.Z, th);

        var sb = new StringBuilder();
        sb.AppendLine($"Wind bearing: {bearing:0.0}° (0=E, 90=S, ±180=W, -90=N)");
        sb.AppendLine($"Strength multiplier: {mag:0.000}");
        sb.AppendLine($"Pressure: {pressure:0.000} ({(pressure > 0 ? "LOW/stormy" : pressure < 0 ? "HIGH/calm" : "neutral")})");
        string biasDesc = field.SeasonalBiasZ < -0.05 ? "SW'ly (toward NE)" : field.SeasonalBiasZ > 0.05 ? "NW'ly (toward SE)" : "due W'ly";
        sb.AppendLine($"Seasonal bias: {field.SeasonalBiasZ:0.00} ({biasDesc})");
        sb.Append($"Active cells: {field.Cells.Count}");
        return TextCommandResult.Success(sb.ToString());
    }

    private static TextCommandResult Cells(TextCommandCallingArgs args, ICoreServerAPI api, WindField field)
    {
        if (field.Cells.Count == 0) return TextCommandResult.Success("No active pressure systems.");
        double th = api.World.Calendar.TotalHours;
        double elapsed = th - field.AnchorHours;
        var sb = new StringBuilder();
        sb.AppendLine($"{field.Cells.Count} active pressure system(s):");
        foreach (PressureCell c in field.Cells)
        {
            double cx = c.X + c.VelX * elapsed;
            double cz = c.Z + c.VelZ * elapsed;
            sb.AppendLine($"  {(c.IsLow ? "LOW " : "HIGH")} s={c.Strength:0.00} r={c.Radius:0} at ({cx:0},{cz:0}) dies in {Math.Max(0, c.DiesTotalHours - th):0.0}h");
        }
        return TextCommandResult.Success(sb.ToString().TrimEnd());
    }
}
