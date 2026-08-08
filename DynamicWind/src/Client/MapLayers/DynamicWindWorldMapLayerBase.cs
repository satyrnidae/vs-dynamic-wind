using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind.Client.MapLayers;

/// <summary>
/// Shared plumbing for the client-side wind/weather map overlays (<see cref="WindArrowMapLayer"/>,
/// <see cref="DopplerRadarMapLayer"/>, <see cref="FrontMapLayer"/>): the resample-cache timer, the
/// world-to-screen projection helper, the standard shader-setup boilerplate, the world-map-only
/// (not-minimap) guard, an RGBA lerp helper, and per-layer legend-toggle persistence.
/// </summary>
public abstract class DynamicWindWorldMapLayerBase : MapLayer
{
    private static readonly FieldInfo? fSnapToPlayer = AccessTools.Field(typeof(GuiElementMap), "snapToPlayer");

    /// <summary>Seconds between resamples (default 2 fps, matches wind arrows/Doppler/fronts).</summary>
    protected virtual double RecomputeInterval => 0.5;

    private double accum;
    private bool haveCache;

    // The base MapLayer.Active property is purely in-memory (the world map dialog's legend checkbox just
    // sets it directly - see WorldMapManager/GuiDialogWorldMap - with no persistence of its own, for
    // vanilla layers either); a game restart otherwise always resets every layer back to whatever OnLoaded
    // sets it to. RestoreActiveState/PersistActiveState round-trip it through a client setting instead, so
    // toggling one of our layers off in the legend actually stays off next session.
    private string ActiveSettingsKey => "dynamicwind-maplayer-active-" + LayerGroupCode;

    protected DynamicWindWorldMapLayerBase(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

    /// <summary>Sets <see cref="MapLayer.Active"/> from the previously-persisted client setting for this
    /// layer, if any, falling back to <paramref name="configDefault"/> (the mod config's own
    /// "visible by default" value) the first time this layer has ever been toggled. Call from
    /// <c>OnLoaded</c> in place of assigning <c>Active</c> directly.</summary>
    protected void RestoreActiveState(bool configDefault)
    {
        Active = api is ICoreClientAPI capi && capi.Settings.Bool.Exists(ActiveSettingsKey)
            ? capi.Settings.Bool[ActiveSettingsKey]
            : configDefault;
        activeAtRestore = Active;
        restored = true;
    }

    private bool activeAtRestore;
    private bool restored;

    /// <summary>Persists the current <see cref="MapLayer.Active"/> value so it survives a game restart -
    /// called whenever the world map dialog closes, which is when the legend checkboxes the user might
    /// have just clicked actually take effect. Only writes when the value actually CHANGED since it was
    /// restored: an unconditional write would persist the config-derived default on the very first map
    /// close, permanently shadowing the server-synced "visible by default" setting for anyone who has
    /// ever opened the map, even though they never touched the legend.</summary>
    public override void OnMapClosedClient()
    {
        if (api is not ICoreClientAPI capi) return;
        if (!restored || Active == activeAtRestore) return;
        capi.Settings.Bool.Set(ActiveSettingsKey, Active, false);
        activeAtRestore = Active;
    }

    /// <summary>True if this GUI map element is the minimap HUD (snaps to the player), i.e. NOT the full world map.</summary>
    protected static bool IsMinimap(GuiElementMap mapElem) => fSnapToPlayer?.GetValue(mapElem) is bool snap && snap;

    /// <summary>Advances the resample-cache timer by <paramref name="dt"/> and calls <see cref="Resample"/> when due.</summary>
    protected void TickResample(float dt, GuiElementMap mapElem)
    {
        accum += dt;
        if (!haveCache || accum >= RecomputeInterval)
        {
            accum = 0;
            Resample(mapElem);
            haveCache = true;
        }
    }

    /// <summary>Recomputes this layer's cached render data for the current map view.</summary>
    protected abstract void Resample(GuiElementMap mapElem);

    /// <summary>Binds the standard flat-quad overlay shader (engine shader 17) with the usual
    /// extraGlow/applyColor/noTexture uniforms, the given texture, and the current projection matrix.</summary>
    protected static IShaderProgram BindStandardShader(ICoreClientAPI capi, int texId)
    {
        IShaderProgram shader = capi.Render.GetEngineShader((EnumShaderProgram)17);
        shader.Uniform("extraGlow", 0);
        shader.Uniform("applyColor", 0);
        shader.Uniform("noTexture", 0f);
        shader.BindTexture2D("tex2d", texId, 0);
        shader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
        return shader;
    }

    /// <summary>World (x,z) -> screen px within the map element; false if outside the visible bounds.</summary>
    protected static bool ToScreen(ElementBounds b, Cuboidd vb, double wx, double wz, out float sx, out float sy)
    {
        double fx = (wx - vb.X1) / (vb.X2 - vb.X1);
        double fz = (wz - vb.Z1) / (vb.Z2 - vb.Z1);
        sx = (float)(b.renderX + fx * b.InnerWidth);
        sy = (float)(b.renderY + fz * b.InnerHeight);
        return fx >= -0.05 && fx <= 1.05 && fz >= -0.05 && fz <= 1.05;
    }

    protected static Vec4f Lerp(Vec4f a, Vec4f b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    /// <summary>Loads a texture from <c>textures/icons/&lt;name&gt;.png</c> in this mod's own assets.</summary>
    protected static int LoadIconTexture(ICoreClientAPI capi, string name)
        => capi.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/" + name + ".png"));

    /// <summary>Doubles <paramref name="step"/> from <paramref name="baseStep"/> until the visible
    /// <paramref name="spanX"/> x <paramref name="spanZ"/> world area, sampled at that step, would cost no
    /// more than <paramref name="maxCells"/> grid cells - so resolution scales down automatically as the
    /// player zooms out instead of the sample cost growing unbounded.</summary>
    protected static double CoarsenStep(double baseStep, double spanX, double spanZ, int maxCells)
    {
        double step = baseStep;
        while (spanX / step * (spanZ / step) > maxCells) step *= 2;
        return step;
    }
}
