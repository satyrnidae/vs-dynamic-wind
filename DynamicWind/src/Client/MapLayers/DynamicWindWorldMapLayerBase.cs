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
/// world→screen projection helper, the standard shader-setup boilerplate, the world-map-only
/// (not-minimap) guard, and an RGBA lerp helper.
/// </summary>
public abstract class DynamicWindWorldMapLayerBase : MapLayer
{
    private static readonly FieldInfo? fSnapToPlayer = AccessTools.Field(typeof(GuiElementMap), "snapToPlayer");

    /// <summary>Seconds between resamples (default 2 fps, matches wind arrows/Doppler/fronts).</summary>
    protected virtual double RecomputeInterval => 0.5;

    private double accum;
    private bool haveCache;

    protected DynamicWindWorldMapLayerBase(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

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

    /// <summary>World (x,z) → screen px within the map element; false if outside the visible bounds.</summary>
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
}
