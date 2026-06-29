using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace DynamicWind;

/// <summary>
/// "Doppler radar" map overlay: a grid of translucent cells tinted by the actual precipitation level
/// (<see cref="WeatherSystemBase.GetPrecipitation(Vec3d)"/>) at each cell's world midpoint. Colours ramp
/// green → yellow → orange → red as rain intensifies toward thunderstorm. Precipitation is only known
/// for loaded chunks, so — like real radar — it shows weather around the player and fades out at range.
/// Renders on both the world map and the minimap; toggle via the map legend.
/// </summary>
public class DopplerRadarMapLayer : MapLayer
{
    public const int Grid = 32;
    private const double RecomputeInterval = 0.4;
    private const float MinPrecip = 0.03f; // below this → dry, not drawn

    // Intensity ramp stops (rain → thunderstorm).
    private static readonly Vec4f Green = Rgb(0x3D, 0xFF, 0x87);
    private static readonly Vec4f Yellow = Rgb(0xFF, 0xFB, 0x11);
    private static readonly Vec4f Orange = Rgb(0xFF, 0x90, 0x28);
    private static readonly Vec4f Red = Rgb(0xFF, 0x3F, 0x26);

    private ICoreClientAPI? capi;
    private MeshRef? quadModel;
    private int whiteTexId;
    private WeatherSystemBase? weather;
    private readonly Matrixf mvMat = new();

    private readonly Vec4f[] cColor = new Vec4f[Grid * Grid];
    private readonly bool[] cVisible = new bool[Grid * Grid];
    private double accum;
    private bool haveCache;

    public DopplerRadarMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink) { }

    public override string Title => "Doppler radar";
    public override string LayerGroupCode => "dynamicwindradar";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    public override void OnLoaded()
    {
        if (api.Side != EnumAppSide.Client) return;
        capi = (ICoreClientAPI)api;
        quadModel = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        whiteTexId = capi.Render.GetOrLoadTexture(new AssetLocation("dynamicwind", "textures/icons/whitecell.png"));
        weather = capi.ModLoader.GetModSystem<WeatherSystemClient>();
        Active = false; // off by default; the map legend toggles it on
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active || capi == null || quadModel == null || weather == null) return;

        accum += dt;
        if (!haveCache || accum >= RecomputeInterval)
        {
            accum = 0;
            Resample(mapElem);
            haveCache = true;
        }

        ElementBounds b = mapElem.Bounds;
        float cellW = (float)(b.InnerWidth / Grid);
        float cellH = (float)(b.InnerHeight / Grid);
        float halfW = cellW * 0.5f;
        float halfH = cellH * 0.5f;
        float renderX = (float)b.renderX;
        float renderY = (float)b.renderY;

        IShaderProgram shader = capi.Render.GetEngineShader((EnumShaderProgram)17);
        shader.Uniform("extraGlow", 0);
        shader.Uniform("applyColor", 0);
        shader.Uniform("noTexture", 0f);
        shader.BindTexture2D("tex2d", whiteTexId, 0);
        shader.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);

        for (int row = 0; row < Grid; row++)
        {
            for (int col = 0; col < Grid; col++)
            {
                int idx = row * Grid + col;
                if (!cVisible[idx]) continue;

                float px = renderX + (col + 0.5f) * cellW;
                float py = renderY + (row + 0.5f) * cellH;

                shader.Uniform("rgbaIn", cColor[idx]);
                mvMat.Set(capi.Render.CurrentModelviewMatrix)
                    .Translate(px, py, 58f)
                    .Scale(halfW, halfH, 0f);
                shader.UniformMatrix("modelViewMatrix", mvMat.Values);
                capi.Render.RenderMesh(quadModel);
            }
        }
    }

    private void Resample(GuiElementMap mapElem)
    {
        Cuboidd vb = mapElem.CurrentBlockViewBounds;
        double playerY = capi!.World.Player?.Entity?.Pos.Y ?? capi.World.SeaLevel;
        double th = capi.World.Calendar.TotalDays;
        var pos = new Vec3d();

        for (int row = 0; row < Grid; row++)
        {
            double fz = (row + 0.5) / Grid;
            double wz = vb.Z1 + fz * (vb.Z2 - vb.Z1);
            for (int col = 0; col < Grid; col++)
            {
                double fx = (col + 0.5) / Grid;
                double wx = vb.X1 + fx * (vb.X2 - vb.X1);
                int idx = row * Grid + col;

                pos.Set(wx, playerY, wz);
                float precip = weather!.GetPrecipitation(pos.X, pos.Y, pos.Z, th);
                if (precip < MinPrecip) { cVisible[idx] = false; continue; }

                float t = precip > 1f ? 1f : precip;
                cColor[idx] = RampColor(t, 0.35f + 0.45f * t); // more opaque toward thunderstorm
                cVisible[idx] = true;
            }
        }
    }

    /// <summary>4-stop intensity ramp (green → yellow → orange → red) at fraction <paramref name="t"/>.</summary>
    private static Vec4f RampColor(float t, float alpha)
    {
        Vec4f c;
        if (t < 1f / 3f) c = Lerp(Green, Yellow, t * 3f);
        else if (t < 2f / 3f) c = Lerp(Yellow, Orange, (t - 1f / 3f) * 3f);
        else c = Lerp(Orange, Red, (t - 2f / 3f) * 3f);
        return new Vec4f(c.R, c.G, c.B, alpha);
    }

    private static Vec4f Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);
    private static Vec4f Lerp(Vec4f a, Vec4f b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    public override void Dispose()
    {
        quadModel?.Dispose();
        quadModel = null;
    }
}
