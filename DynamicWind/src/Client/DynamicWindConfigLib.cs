using System;
using ConfigLib;
using DynamicWind.Config;
using Vintagestory.API.Common;

namespace DynamicWind.Client;

/// <summary>
/// Optional integration with the <c>configlib</c> mod, isolated here so the only code that touches
/// ConfigLib types lives in one class. The CLR resolves a method's type references when it is first
/// JIT-compiled, so as long as <see cref="TryRegister"/> is never called when ConfigLib is absent
/// (guarded by <c>IsModEnabled</c> in <see cref="DynamicWindModClient"/>), the missing
/// <c>configlib.dll</c> can't cause a type-load failure — a genuinely optional dependency with no
/// modinfo entry.
///
/// Config is server-authoritative, so a GUI edit does NOT apply locally; it sends a privileged
/// request to the server, which validates, applies, and broadcasts the result back (handled by the
/// client's normal config-sync path). In singleplayer the host is privileged, so it round-trips
/// immediately.
/// </summary>
internal static class DynamicWindConfigLib
{
    public const string ConfigLibModId = "configlib";

    /// <param name="requestServerApply">Invoked when the user edits a setting; should send the config to the server.</param>
    /// <param name="applyLocal">Invoked to apply the config object to local state (pull-once / on ConfigLib server sync).</param>
    public static void TryRegister(ICoreAPI api, DynamicWindConfig config, Action requestServerApply, Action applyLocal, ILogger logger)
    {
        ConfigLibModSystem configLib = api.ModLoader.GetModSystem<ConfigLibModSystem>();
        if (configLib == null) return;

        string domain = DynamicWindModCommon.Domain;

        // Deliberately NOT DynamicWindModCommon.ConfigFileName: ConfigLib resolves its own `path` to
        // the exact same ModConfig/<file> location our mod's own LoadModConfig/StoreModConfig use. In
        // singleplayer the server round-trip (see DynamicWindModServer.OnConfigUpdateRequest) writes
        // that file on every settings change with whatever it has in memory for the clientside-only
        // (ProtoIgnore, never transmitted) properties — near-instantly clobbering whatever ConfigLib
        // just wrote for those properties and reverting them once something re-reads the file.
        configLib.RegisterCustomManagedConfig(
            domain,
            config,
            path: "dynamicwind-configlib.json",
            onSyncedFromServer: applyLocal,
            onSettingChanged: _ => requestServerApply());

        // ConfigLib pushes parsed file values into the object only on later edits, not at registration,
        // so pull them in once now and apply locally.
        configLib.GetConfig(domain)?.AssignSettingsValues(config);
        applyLocal();

        logger.Notification("[dynamicwind] ConfigLib integration enabled.");
    }
}
