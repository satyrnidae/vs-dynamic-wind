using System;
using DynamicWind.Config;
using DynamicWind.Network;
using DynamicWind.WindSystem;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind;

/// <summary>
/// Shared base for the server and client mod systems (three-class split modelled on
/// <c>D:\satyrnidae\vs-balladeer-class</c>). Loads/validates the config, registers the single
/// dedicated network channel and its message types, and owns the shared deterministic
/// <see cref="WindField"/> plus the directional <see cref="OnGetWindSpeed"/> hook.
/// <see cref="ShouldLoad"/> returns false here so only the concrete server/client subclass loads
/// on its side.
/// </summary>
public abstract class DynamicWindModCommon : ModSystem
{
    public const string Domain = "dynamicwind";
    public const string ConfigFileName = "dynamicwind.json";
    public const string ChannelName = "dynamicwind";

    protected ICoreAPI Api { get; private set; } = null!;
    public DynamicWindConfig Config { get; protected set; } = new();
    protected INetworkChannel Channel { get; private set; } = null!;
    protected readonly WindField Field = new();

    public override bool ShouldLoad(EnumAppSide side) => false;

    // Load after the vanilla weather system so our OnGetWindSpeed handler is appended after its (it
    // sets windSpeed.X to the scalar strength, which we read as the magnitude and redirect).
    public override double ExecuteOrder() => 1.0;

    public override void Start(ICoreAPI api)
    {
        Api = api;
        LoadConfig(api);
        ApplyConfigToField();

        Channel = (api.Network.GetChannel(ChannelName) ?? api.Network.RegisterChannel(ChannelName))
            .RegisterMessageType(typeof(DynamicWindConfig))
            .RegisterMessageType(typeof(WindFieldState))
            .RegisterMessageType(typeof(ConfigUpdateRequestPacket));
    }

    protected void LoadConfig(ICoreAPI api)
    {
        try
        {
            Config = api.LoadModConfig<DynamicWindConfig>(ConfigFileName) ?? new DynamicWindConfig();
        }
        catch (Exception e)
        {
            Mod.Logger.Error("[dynamicwind] Failed to load config, using defaults: {0}", e);
            Config = new DynamicWindConfig();
        }
        Config.Validate();
        try { api.StoreModConfig(Config, ConfigFileName); }
        catch (Exception e) { Mod.Logger.Warning("[dynamicwind] Failed to store config: {0}", e); }
    }

    /// <summary>Copy the scalar field parameters from the config into the shared WindField. Cells untouched.</summary>
    public void ApplyConfigToField()
    {
        Field.BaseStrength = Config.BaseJetstreamStrength;
        Field.MeanderAmplitude = Config.JetstreamMeanderAmplitude;
        Field.MeanderFrequency = Config.JetstreamMeanderFrequency;
        Field.CyclonicInflowAngle = Config.CyclonicInflowAngleDeg * GameMath.DEG2RAD;
        Field.CirculationStrength = Config.CirculationStrength;
        Field.SeasonalBiasMaxAngle = Config.SeasonalBias ? Config.SeasonalBiasMaxAngleDeg * GameMath.DEG2RAD : 0.0;
        Field.JetNoiseAmplitude = Config.JetstreamNoiseAmplitude;
        Field.GradientWindScale = Config.PressureWindStrength;
        Field.OrographicEnabled = Config.OrographicEnabled;
        Field.OroSlowdown = Config.OrographicSlowdownFactor;
        Field.OroDeflection = Config.OrographicDeflectionStrength;
        Field.OroWake = Config.OrographicWakeFactor;
        Field.AltitudeMaxDrop = Config.AltitudePressureDrop;
        Field.ThermalStaticScale = Config.ThermalStaticScale;
    }

    /// <summary>
    /// Directional wind hook. Vanilla's weather system runs first and stores its own scalar wind speed
    /// (SimplexNoise over time, not diurnal) in <c>windSpeed.X</c>. We keep that as the baseline
    /// jetstream speed and just point it along the true wind direction from <see cref="WindField.WindAt"/>;
    /// then pressure fronts (cells/gradients that push the field magnitude above the calm baseline) ADD
    /// wind on top. This is additive, NOT multiplicative — so a calm vanilla pattern can't zero the wind
    /// globally, fronts still blow when vanilla is calm, and an area with no pressure differential simply
    /// falls back to vanilla's own (possibly near-zero) speed. Every consumer that calls
    /// <c>GetWindSpeedAt</c> (particles, foliage, blowing snow, dropped items, ground-movement) follows it.
    /// </summary>
    protected void OnGetWindSpeed(Vec3d pos, ref Vec3d windSpeed)
    {
        double vsSpeed = windSpeed.X; // vanilla's own wind-speed variation
        double th = Api.World.Calendar.TotalHours;
        Vec2d w = Field.WindAt(pos.X, pos.Z, th);

        double mag = w.Length();
        if (mag < 1e-6) return; // degenerate field → leave vanilla value

        // Baseline jetstream speed = vanilla's, modulated by a slow jet-streak noise (faster/slower
        // segments, never zero). Pressure fronts then ADD wind on top of that.
        double frontExcess = mag - Field.BaseStrength;
        if (frontExcess < 0) frontExcess = 0;
        double speed = vsSpeed * Field.JetStreak(pos.X, pos.Z, th) + Config.FrontWindStrength * frontExcess;

        windSpeed.X = speed * w.X / mag;
        windSpeed.Z = speed * w.Y / mag;
    }

    // Re-entrancy guard: the terrain-thermal term below calls ThermalPressureFunc → TerrainThermal →
    // GetClimateAt, which re-fires OnGetClimate. Without this we'd recurse until the stack blew. On a
    // nested call we skip our shift entirely (the inner climate is only used to derive thermal
    // pressure; it must not carry our temperature delta anyway).
    [ThreadStatic] private static bool inClimateHandler;

    /// <summary>
    /// Pressure → air temperature. A low (signed pressure +) warms the air, a high (−) cools it, up to
    /// <see cref="DynamicWindConfig.FrontTemperatureMax"/> °C at peak intensity; the terrain-thermal
    /// field (warm dry land / cool deep water) adds a gentler shift up to
    /// <see cref="DynamicWindConfig.ThermalTemperatureMax"/> °C. Both terms are read from the same
    /// synced <see cref="WindField"/> used everywhere else, so server and client agree. Worldgen base
    /// climate is left untouched — only live ("now"/dated) lookups shift.
    /// </summary>
    protected void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
    {
        if (climate == null || mode == EnumGetClimateMode.WorldGenValues) return;
        if (inClimateHandler) return;
        if (Config.FrontTemperatureMax <= 0f && Config.ThermalTemperatureMax <= 0f) return;

        inClimateHandler = true;
        try
        {
            double totalHours = totalDays * Api.World.Calendar.HoursPerDay;

            // Front term: signed cell pressure (+ low / − high), normalised by the sim's strength cap.
            double p = Field.CellPressureAt(pos.X, pos.Z, totalHours);
            double frontDelta = Config.FrontTemperatureMax * Clamp1(p / TempPressureNorm);

            // Terrain thermal term: + warm land / − cool water.
            double thermalDelta = 0;
            if (Config.ThermalTemperatureMax > 0f && Field.ThermalPressureFunc != null)
            {
                double thermal = Field.ThermalPressureFunc(pos.X, pos.Z, totalHours);
                thermalDelta = Config.ThermalTemperatureMax * Clamp1(thermal / ThermalNorm);
            }

            climate.Temperature += (float)(frontDelta + thermalDelta);
        }
        finally
        {
            inClimateHandler = false;
        }
    }

    // Normalisers: a peak system (|strength| == WindFieldSimulation.MaxStrength = 3) reaches the full
    // FrontTemperatureMax; typical terrain thermals (|TerrainThermal| up to ~2.6) reach ~ThermalTemperatureMax.
    private const double TempPressureNorm = 3.0;
    private const double ThermalNorm = 2.0;

    private static double Clamp1(double v) => v < -1 ? -1 : (v > 1 ? 1 : v);
}
