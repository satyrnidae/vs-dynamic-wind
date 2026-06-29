using System;
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
        Field.CyclonicInflowAngle = Config.CyclonicInflowAngleDeg * GameMath.DEG2RAD;
        Field.SeasonalBiasMaxAngle = Config.SeasonalBias ? Config.SeasonalBiasMaxAngleDeg * GameMath.DEG2RAD : 0.0;
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
        Vec2d w = Field.WindAt(pos.X, pos.Z, Api.World.Calendar.TotalHours);

        double mag = w.Length();
        if (mag < 1e-6) return; // degenerate field → leave vanilla value

        // Front contribution: how far the field magnitude exceeds the calm baseline (cells, gradients).
        double frontExcess = mag - Field.BaseStrength;
        if (frontExcess < 0) frontExcess = 0;
        double speed = vsSpeed + Config.FrontWindStrength * frontExcess;

        windSpeed.X = speed * w.X / mag;
        windSpeed.Z = speed * w.Y / mag;
    }
}
