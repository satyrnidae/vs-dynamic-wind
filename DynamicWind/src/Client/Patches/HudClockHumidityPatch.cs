using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using DynamicWind.WindSystem;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace DynamicWind.Client.Patches;

/// <summary>
/// Optional runtime patch adding a dedicated "RH: NN%" line to the locally-installed third-party "HUD
/// Clock" mod (modid <see cref="HudClockWindPatch.HudClockModId"/>), sitting alongside
/// <see cref="HudClockWindPatch"/> (wind) rather than replacing HUD Clock's own existing "Rainfall" line
/// (which already reflects our humidity for free once <see cref="DynamicWindModCommon.OnGetClimate"/>
/// blends it into live <c>Rainfall</c> - see CLAUDE.md humidity design notes).
///
/// HUD Clock's lines are driven by a closed, internal <c>MainHudLineKey</c> enum: <c>VisibleLineKeys()</c>
/// (a yield-iterator) decides which keys are visible, <c>GetLineText(key)</c> switches on the key for
/// display text, and <c>LineKeyToElementName(key)</c> turns a key into a GUI element name (a plain string
/// concat, safe for any value). We splice in a synthetic key value the mod's own enum never declares
/// (<see cref="SyntheticKeyValue"/>) via two postfixes:
///   1. <c>VisibleLineKeys()</c> - appends the synthetic key when enabled. Because the real return type
///      (<c>IEnumerable&lt;MainHudLineKey&gt;</c>) is an internal enum we can't reference at compile time,
///      the postfix parameter is the non-generic <see cref="IEnumerable"/>, and the splice itself uses
///      <c>Enumerable.Append&lt;T&gt;</c> invoked reflectively so the replacement's runtime type still
///      genuinely implements the caller's expected generic interface (required - the caller's foreach was
///      compiled against that exact type).
///   2. <c>GetLineText(key)</c> - <c>key</c> is declared <see cref="object"/> (Harmony boxes/widens
///      automatically for inaccessible value types); returns our formatted text for the synthetic value.
/// <c>LineKeyToElementName</c> needs no patch - its plain string-concat implementation already produces a
/// stable, unique name for any integer value, including ours.
///
/// This is the most fragile patch in the feature (it reaches past simple text overwrites into HudClock's
/// iterator-driven layout mechanism via reflection), so - like <see cref="Compat.ImmersiveBodyTemperatureCompat"/>
/// - every target is resolved via <see cref="AccessTools"/> and no-ops with a warning if anything is
/// missing, and <see cref="Config.DynamicWindConfig.HumidityHudClockLineEnabled"/> is checked live on every
/// call so it can be toggled off without a restart.
///
/// Known caveat, not fixed here: HUD Clock's own <c>MainHudViewModel.IsEmpty</c> (which decides whether the
/// whole clock HUD closes) only checks its fixed set of built-in "IsXVisible" flags, not our synthetic line
/// - a player who disables every other HUD Clock line would see the HUD stay closed even with ours
/// "visible." Narrow edge case (requires disabling literally everything else in HUD Clock's settings); not
/// worth a further patch into <c>IsEmpty</c>.
/// </summary>
internal static class HudClockHumidityPatch
{
    // Well clear of HUD Clock's own declared MainHudLineKey range (10 real members, 0-9).
    private const int SyntheticKeyValue = 9001;

    private static ICoreClientAPI capi = null!;
    private static Type lineKeyType = null!;
    private static MethodInfo appendMethod = null!;

    public static void Apply(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        try
        {
            Type? vmType = AccessTools.TypeByName("HudClock.Presentation.MainHud.MainHudViewModel");
            Type? keyType = AccessTools.TypeByName("HudClock.Presentation.MainHud.MainHudLineKey");
            MethodInfo? visibleLineKeys = vmType == null ? null : AccessTools.Method(vmType, "VisibleLineKeys");
            MethodInfo? getLineText = (vmType == null || keyType == null) ? null : AccessTools.Method(vmType, "GetLineText", new[] { keyType });

            if (vmType == null || keyType == null || visibleLineKeys == null || getLineText == null)
            {
                logger.Warning("[dynamicwind] HUD Clock humidity-line patch target not found; skipping.");
                return;
            }

            capi = api;
            lineKeyType = keyType;
            appendMethod = typeof(Enumerable).GetMethod("Append")!.MakeGenericMethod(keyType);

            harmony.Patch(visibleLineKeys, postfix: new HarmonyMethod(typeof(HudClockHumidityPatch), nameof(VisibleLineKeysPostfix)));
            harmony.Patch(getLineText, postfix: new HarmonyMethod(typeof(HudClockHumidityPatch), nameof(GetLineTextPostfix)));

            logger.Notification("[dynamicwind] HUD Clock RH (humidity) line enabled.");
        }
        catch (Exception e)
        {
            logger.Warning("[dynamicwind] Failed to patch HUD Clock humidity line: {0}", e);
        }
    }

    private static bool Enabled => DynamicWindRuntime.ClientConfig?.HumidityHudClockLineEnabled ?? false;

    private static void VisibleLineKeysPostfix(ref IEnumerable __result)
    {
        if (!Enabled) return;
        object syntheticKey = Enum.ToObject(lineKeyType, SyntheticKeyValue);
        __result = (IEnumerable)appendMethod.Invoke(null, new[] { __result, syntheticKey })!;
    }

    private static void GetLineTextPostfix(object key, ref string? __result)
    {
        if (!Enabled || key == null || Convert.ToInt32(key) != SyntheticKeyValue) return;

        WindField? field = DynamicWindRuntime.ClientField;
        if (field == null) return;

        Entity? plr = capi.World.Player?.Entity;
        if (plr == null) { __result = null; return; }

        double totalHours = capi.World.Calendar.TotalHours;
        double humidity = field.HumidityAt(plr.Pos.X, plr.Pos.Z, totalHours);
        __result = Lang.Get("dynamicwind:hudclock-humidity-line", $"{humidity * 100:0}");
    }
}
