using System.Collections.Generic;
using Cairo;
using DynamicWind.Entities;
using DynamicWind.Patches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace DynamicWind.Client.Gui;

/// <summary>
/// Cairo-drawn scrolling oscilloscope of the sailboat thrust MULT terms over time - the raw values added
/// to/subtracted from the 1.0 baseline in <c>speed = vanillaSpeed * (1.0 + mult)</c> (see
/// <see cref="SailboatWind.WindBoost"/>/<see cref="SailboatWind.AngleBoost"/>), sampled once per rendered
/// frame over the last <see cref="WindowMs"/>. Exists because verifying the trim curve by re-running
/// <c>.dwind sail</c> repeatedly while spinning/trimming is slow and hard to judge from numbers alone -
/// this lets you watch all three values respond live, like a heart-rate monitor.
///
/// Plots the RAW mult terms, deliberately independent of <c>sailPosition</c> (furled/half/full) - NOT
/// because half and full sail perform identically (vanilla's own base sail-force term already differs
/// between them, linear in `sailPosition` before this mod's multiplier runs - see
/// <see cref="SailboatWind.FactorForCosGamma"/>) but because this mod's own factor shouldn't ALSO scale by
/// sail position on top of vanilla's already-correct baseline. An earlier version plotted the
/// sailPosition-scaled factor anyway, which swung the combined line hard on every furl/unfurl even with
/// wind and trim flat - read as "combined is broken" when it was just tracking furl state (and doubly
/// penalized a half-raised sail: vanilla's halving plus this mod's).
///
/// The cyan "trim" line plots <see cref="SailboatWind.SailState.TrimMult"/>, which already reflects
/// <see cref="SailboatWind.CrosswindBlendedAngleBoost"/> - so it can flatten toward
/// <c>SailCrosswindEfficiency</c> as trim passes 45 deg, instead of swinging to a raw cosGamma extreme.
///
/// HUD dialog type (like the built-in tick profiler): never grabs input focus, so it stays open while
/// actively steering/trimming with the mouse ungrabbed. Toggled only via <c>.dwind sailgraph</c> (no
/// keybind), redrawn every frame while open (<see cref="OnRenderGUI"/>) since the point is watching it
/// change live.
/// </summary>
internal sealed class GuiDialogSailGraph : GuiDialog
{
    private const int GraphWidth = 340;
    private const int GraphHeight = 204;

    private const long WindowMs = 10_000; // width of the scrolling time window

    // Fixed rather than autoscaled: autoscaling let the combined line's larger swing visually flatten the
    // wind/trim lines even though they were changing correctly. +-60% comfortably bounds the model's real
    // range (worst case ~-50% at dead calm/dead headwind).
    private const double MultAxisMin = -0.60;
    private const double MultAxisMax = 0.60;

    // Secondary gridlines/labels at these levels, in addition to the 0% baseline.
    private static readonly double[] TickMarks = { -0.25, -0.10, 0.10, 0.25 };

    // Distinct colors per line: green = wind-speed term, cyan = trim/angle term, orange = combined
    // (unscaled either way, see class remarks).
    private static readonly double[] WindColor = { 0.45, 0.9, 0.45, 0.9 };
    private static readonly double[] TrimColor = { 0.4, 0.75, 1.0, 0.9 };
    private static readonly double[] CombinedColor = { 1.0, 0.6, 0.15, 0.95 };

    #region Sample buffer

    private readonly struct Sample
    {
        public readonly long T;
        public readonly float Wind, Trim, Combined;
        public Sample(long t, float wind, float trim, float combined) { T = t; Wind = wind; Trim = trim; Combined = combined; }
    }

    // Queue, not List: samples are appended at the back and expired off the front every frame, needing
    // O(1) removal-from-front rather than a shifting array.
    private readonly Queue<Sample> samples = new();

    private void Expire(long now)
    {
        while (samples.Count > 0 && samples.Peek().T < now - WindowMs) samples.Dequeue();
    }

    #endregion

    private readonly CairoFont labelFont = CairoFont.WhiteSmallText();
    private readonly CairoFont axisFont = CairoFont.WhiteDetailText().WithColor(new double[] { 1, 1, 1, 0.6 });

    public override string ToggleKeyCombinationCode => null!;
    public override bool Focusable => false;
    public override EnumDialogType DialogType => EnumDialogType.HUD;

    #region Dialog lifecycle

    public GuiDialogSailGraph(ICoreClientAPI capi) : base(capi)
    {
        ElementBounds inner = ElementBounds.Fixed(EnumDialogArea.None, 0, 0, GraphWidth, GraphHeight);
        ElementBounds bg = inner.ForkBoundingParent(6, 6, 6, 6);
        ElementBounds dialogPos = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.RightMiddle)
            .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

        SingleComposer = capi.Gui.CreateCompo("dwindsailgraph", dialogPos)
            .AddGameOverlay(bg)
            .AddDynamicCustomDraw(inner, OnDraw, "graph")
            .Compose();
    }

    /// <summary>Redraws every frame while open - a static custom-draw element (the default) only composes
    /// once, which would freeze the plot at whatever it looked like on open.</summary>
    public override void OnRenderGUI(float deltaTime)
    {
        SingleComposer.GetCustomDraw("graph")?.Redraw();
        base.OnRenderGUI(deltaTime);
    }

    #endregion

    #region Cairo drawing

    private void OnDraw(Context ctx, ImageSurface surface, ElementBounds currentBounds)
    {
        const double marginL = 34, marginR = 8, marginT = 8, marginB = 34;
        const double plotW = GraphWidth - marginL - marginR;
        const double plotH = GraphHeight - marginT - marginB;

        long now = capi.ElapsedMilliseconds;
        Expire(now);

        SailboatWind.SailState diag = default;
        string? status = null;
        Entity? mount = ((capi.World?.Player?.Entity?.MountedOn) as IMountableSeat)?.Entity;
        if (mount is not EntityBoat boat) status = "not sitting in a sailboat";
        else
        {
            // Prefer the behaviour's once-per-tick cache over recomputing: GetWindSpeedAt runs the whole
            // OnGetWindSpeed hook chain (front excess, orographic, occlusion), and this draw runs every
            // rendered frame while open - recomputing from scratch would triple up on that cost alongside
            // SailboatWind.Postfix's own per-tick call.
            EntityBehaviorSailTrim? trim = boat.GetBehavior<EntityBehaviorSailTrim>();
            diag = trim != null ? trim.LastState : SailboatWind.Evaluate(boat);
            if (!diag.Valid) status = "sailboat wind response disabled";
            else if (!diag.AngleModel) status = "legacy heading model active (no sail angle)";
        }

        // Only sample while the model is actually applicable - a gap in the scrolling lines correctly
        // shows "not tracked right now" rather than holding the last value flat.
        if (status == null)
        {
            samples.Enqueue(new Sample(now, (float)diag.WindMult, (float)diag.TrimMult, (float)diag.CombinedMult));
        }

        double PX(long t) => marginL + (double)(t - (now - WindowMs)) / WindowMs * plotW;
        double PY(double v) => marginT + plotH - (v - MultAxisMin) / (MultAxisMax - MultAxisMin) * plotH;

        DrawAxes(ctx, marginL, marginT, plotW, plotH, PY);

        if (samples.Count >= 2)
        {
            PlotLine(ctx, WindColor, PX, PY, s => s.Wind);
            PlotLine(ctx, TrimColor, PX, PY, s => s.Trim);
            PlotLine(ctx, CombinedColor, PX, PY, s => s.Combined);
        }

        DrawLegend(ctx, marginL, GraphHeight - 22);
    }

    private void PlotLine(Context ctx, double[] color, System.Func<long, double> px, System.Func<double, double> py,
        System.Func<Sample, float> value)
    {
        ctx.SetSourceRGBA(color[0], color[1], color[2], color[3]);
        ctx.LineWidth = 1.5;
        bool first = true;
        foreach (Sample s in samples)
        {
            double p = px(s.T), q = py(value(s));
            if (first) { ctx.MoveTo(p, q); first = false; } else ctx.LineTo(p, q);
        }
        ctx.Stroke();
    }

    private void DrawLegend(Context ctx, double x, double y)
    {
        x = DrawLegendEntry(ctx, x, y, WindColor, "wind");
        x = DrawLegendEntry(ctx, x, y, TrimColor, "trim");
        DrawLegendEntry(ctx, x, y, CombinedColor, "combined");
    }

    private double DrawLegendEntry(Context ctx, double x, double y, double[] color, string text)
    {
        ctx.SetSourceRGBA(color[0], color[1], color[2], color[3]);
        ctx.Rectangle(x, y - 6, 10, 3);
        ctx.Fill();

        axisFont.SetupContext(ctx);
        ctx.MoveTo(x + 14, y);
        ctx.ShowText(text);
        return x + 14 + axisFont.GetTextExtents(text).Width + 10;
    }

    private void DrawAxes(Context ctx, double marginL, double marginT, double plotW, double plotH,
        System.Func<double, double> py)
    {
        ctx.SetSourceRGBA(1, 1, 1, 0.3);
        ctx.LineWidth = 1.0;

        // 0% baseline - the more prominent gridline (vanilla's own unmodified thrust).
        double yZero = py(0.0);
        ctx.MoveTo(marginL, yZero);
        ctx.LineTo(marginL + plotW, yZero);
        ctx.Stroke();

        // Secondary tick gridlines at -25%, -10%, 10%, 25%, each fainter and labeled.
        ctx.SetSourceRGBA(1, 1, 1, 0.12);
        foreach (double tick in TickMarks)
        {
            double y = py(tick);
            ctx.MoveTo(marginL, y);
            ctx.LineTo(marginL + plotW, y);
            ctx.Stroke();
        }

        ctx.SetSourceRGBA(1, 1, 1, 0.3);
        ctx.Rectangle(marginL, marginT, plotW, plotH);
        ctx.Stroke();

        axisFont.SetupContext(ctx);
        ctx.MoveTo(2, py(MultAxisMax) + 8);
        ctx.ShowText("+60%");
        ctx.MoveTo(2, py(MultAxisMin));
        ctx.ShowText("-60%");
        foreach (double tick in TickMarks)
        {
            ctx.MoveTo(2, py(tick) + 3);
            ctx.ShowText($"{tick * 100:+0;-0}%");
        }
        ctx.MoveTo(2, yZero + 3);
        ctx.ShowText("0%");

        ctx.MoveTo(marginL + plotW - 30, marginT + plotH + 12);
        ctx.ShowText("-10s .. now");
    }

    private void DrawLabel(Context ctx, double x, double y, string text)
    {
        labelFont.SetupContext(ctx);
        ctx.MoveTo(x, y);
        ctx.ShowText(text);
    }

    #endregion
}
