using System;
using DynamicWind.Config;
using DynamicWind.Network;
using DynamicWind.PressureSystem;
using DynamicWind.TerrainSystem;
using DynamicWind.WindSystem;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

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
    private RoomRegistry? roomRegistry;

    // Front-type gust intensity: an occluded front (its cold front has caught the warm one) brings the
    // highest winds, a cold front heavy gusts, a warm front only light gusts, and a stationary front
    // stays comparatively calm — same regime split as WeatherCoupling's precipitation weighting, applied
    // here as a multiplier on the front-driven EXCESS wind (see ComputeWindSpeed), never on the vanilla
    // baseline itself.
    private const double OccludedWindMult = 1.6;
    private const double ColdWindMult = 1.3;
    private const double WarmWindMult = 0.6;
    private const double StationaryWindMult = 0.3;

    public override bool ShouldLoad(EnumAppSide side) => false;

    // Load after the vanilla weather system so our OnGetWindSpeed handler is appended after its (it
    // sets windSpeed.X to the scalar strength, which we read as the magnitude and redirect).
    public override double ExecuteOrder() => 1.0;

    public override void Start(ICoreAPI api)
    {
        Api = api;
        LoadConfig(api);
        ApplyConfigToField();
        roomRegistry = api.ModLoader.GetModSystem<RoomRegistry>();

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

    /// <summary>Wires <see cref="WindField.ThermalPressureFunc"/> to a cached lookup of <see cref="TerrainThermal.PressureAt"/>. Shared by both sides; terrain-height caching still differs per side and is set up separately.</summary>
    protected void SetupThermalPressureFunc(ICoreAPI api)
    {
        var thermalCache = new ThermalCache((x, z, th) => TerrainThermal.PressureAt(api.World, x, z, th));
        Field.ThermalPressureFunc = thermalCache.Get;
    }

    /// <summary>Wires <see cref="WindField.TerrainHumidityFunc"/> to a cached lookup of <see cref="TerrainHumidity.WorldgenBaselineAt"/>.
    /// Shared by both sides — the worldgen rainfall map is stable, so a coarse tile + effectively-static
    /// time bucket (mirrors the terrain-height caches) keeps this cheap on the wind hot path.</summary>
    protected void SetupTerrainHumidityFunc(ICoreAPI api)
    {
        var humidityCache = new ThermalCache((x, z, _) => TerrainHumidity.WorldgenBaselineAt(api.World, x, z), tile: 32.0, bucketHours: 1e9);
        Field.TerrainHumidityFunc = humidityCache.Get;
    }

    /// <summary>Sets <see cref="WindField.SeaLevel"/>/<see cref="WindField.WorldHeight"/>/<see cref="WindField.MapCenterZ"/>
    /// and wires <see cref="WindField.LatitudeFunc"/> to the real climate latitude (repeating triangle wave —
    /// see "Real latitude" in CLAUDE.md). Re-checked per call rather than captured once, since
    /// <c>Calendar.OnGetLatitude</c> is wired up by world-gen and may not be assigned yet at this exact moment.
    /// Falls back to the old map-midline heuristic until it is. Shared by both sides; call once world geometry
    /// is available (server: <c>SaveGameLoaded</c>, client: <c>LevelFinalize</c>).</summary>
    protected void SetupWorldGeometry(ICoreAPI api)
    {
        Field.SeaLevel = api.World.SeaLevel;
        Field.WorldHeight = api.World.BlockAccessor.MapSizeY;
        Field.MapCenterZ = api.World.BlockAccessor.MapSizeZ / 2.0;
        Field.LatitudeFunc = z => api.World.Calendar.OnGetLatitude != null
            ? api.World.Calendar.OnGetLatitude(z)
            : (z < Field.MapCenterZ ? 1.0 : -1.0);
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
        Field.OroHeightThreshold = Config.OrographicHeightThreshold;
        Field.OroSlowdown = Config.OrographicSlowdownFactor;
        Field.OroDeflection = Config.OrographicDeflectionStrength;
        Field.OroWake = Config.OrographicWakeFactor;
        Field.AltitudeMaxDrop = Config.AltitudePressureDrop;
        Field.ThermalStaticScale = Config.ThermalStaticScale;
        Field.HumidityWaterProximityStrength = Config.HumidityWaterProximityStrength;
        Field.HumidityPressureStrength = Config.HumidityPressureStrength;
        Field.HumidityNoiseAmplitude = Config.HumidityNoiseAmplitude;
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
        double th = Api.World.Calendar.TotalHours;
        Vec3d result = ComputeWindSpeed(pos, windSpeed.X, th, applyOcclusion: true);
        windSpeed.X = result.X;
        windSpeed.Z = result.Z;
    }

    /// <summary>
    /// Core directional wind formula behind <see cref="OnGetWindSpeed"/>, factored out so callers that
    /// need the true wind speed but can't (or shouldn't) pay for the per-position occlusion check — e.g.
    /// <see cref="Client.MapLayers.WindArrowMapLayer"/>, which samples a 24×24 world-map grid where
    /// "indoors" is meaningless and the room/light lookups would be the dominant cost — can pass
    /// <paramref name="applyOcclusion"/> = false and get the same speed/direction sans that factor.
    /// <paramref name="vsSpeed"/> is vanilla's own raw wind-speed noise at <paramref name="pos"/> (e.g.
    /// <c>WeatherSystemBase.WeatherDataSlowAccess.GetWindSpeed(pos)</c>), taken as a parameter so callers
    /// outside the hook chain (which never receive vanilla's pre-computed value) can supply it directly.
    /// </summary>
    public Vec3d ComputeWindSpeed(Vec3d pos, double vsSpeed, double totalHours, bool applyOcclusion)
    {
        Vec2d w = Field.WindAt(pos.X, pos.Z, totalHours, out double oroFactor);

        double mag = w.Length();
        if (mag < 1e-6) return new Vec3d(vsSpeed, 0, 0); // degenerate field → leave vanilla value

        // Front strength is read from the field magnitude BEFORE orographic slowdown, so a system's
        // intensity isn't double-counted once here and again when the terrain factor is applied below.
        double preOroMag = oroFactor > 1e-6 ? mag / oroFactor : mag;

        // Baseline jetstream speed = vanilla's, modulated by a slow jet-streak noise (faster/slower
        // segments, never zero). Pressure fronts then ADD wind on top of that. Orographic interaction
        // (windward climb, lee wake) then scales the WHOLE total down, so terrain slows baseline wind
        // too, not just front-driven excess.
        double frontExcess = preOroMag - Field.BaseStrength;
        if (frontExcess < 0) frontExcess = 0;

        // Weight that excess by which front regime(s) influence this position — see the *WindMult
        // consts above. Falls back to a neutral 1x where no low's influence reaches (plain jetstream/
        // high-pressure gradient wind, which isn't a "front" and shouldn't be gust-differentiated).
        WindField.FrontRegimeBias regime = Field.FrontRegimeBiasAt(pos.X, pos.Z, totalHours);
        double regimeTotal = regime.Cold + regime.Warm + regime.Occluded + regime.Stationary;
        double frontTypeWindMult = regimeTotal > 1e-6
            ? (regime.Occluded * OccludedWindMult + regime.Cold * ColdWindMult
               + regime.Warm * WarmWindMult + regime.Stationary * StationaryWindMult) / regimeTotal
            : 1.0;

        double speed = (vsSpeed * Field.JetStreak(pos.X, pos.Z, totalHours) + Config.FrontWindStrength * frontExcess * frontTypeWindMult) * oroFactor;

        if (applyOcclusion && Config.OcclusionEnabled && !IsAmbientPlayerQuery(pos)) speed *= OcclusionFactor(pos);

        return new Vec3d(speed * w.X / mag, 0, speed * w.Y / mag);
    }

    /// <summary>
    /// True if <paramref name="pos"/> is (X/Z-wise) the client player's own position. Vanilla's
    /// <c>WeatherSystemClient.OnRenderFrame</c> calls <c>GetWindSpeedAt</c> at the player's exact X/Z
    /// every frame (once at the player's own Y, once at the player-column's rain-map-height Y) purely to
    /// smooth <c>GlobalConstants.CurrentWindSpeedClient</c>/<c>CurrentSurfaceWindSpeedClient</c> — a single
    /// value vanilla then broadcasts world-wide for things like the foliage-sway shader uniform, which is
    /// NOT re-queried per leaf/block. If we occluded that call, a player standing in one shed would freeze
    /// foliage sway for the ENTIRE visible world, not just their shed. Real per-position callers (dropped
    /// items, particles, weathervanes) pass their own world position, essentially never the player's exact
    /// X/Z, so this exemption doesn't leak into legitimate occlusion elsewhere.
    /// </summary>
    private bool IsAmbientPlayerQuery(Vec3d pos)
    {
        if (Api is not ICoreClientAPI capi) return false;
        Entity? plr = capi.World.Player?.Entity;
        if (plr == null) return false;
        double dx = pos.X - plr.Pos.X;
        double dz = pos.Z - plr.Pos.Z;
        return dx * dx + dz * dz < 0.01;
    }

    /// <summary>
    /// Shielding from roofs/walls/underground. A position inside a fully enclosed vanilla "room"
    /// (<see cref="RoomRegistry"/> — the same room detection cellars/greenhouses use for cooling/humidity,
    /// <c>ExitCount == 0</c> meaning no gaps to open air) gets flat-zero wind: a glass-roofed greenhouse
    /// still reads bright on <c>OnlySunLight</c>, so the light-based falloff below would miss it, but it
    /// has no opening for outside air to actually move through. Everywhere else falls back to a power-curve
    /// falloff on sun-exposure light level (0 = fully enclosed, <see cref="DynamicWindConfig.OcclusionLightThreshold"/>
    /// or above = fully open): <see cref="DynamicWindConfig.OcclusionSharpness"/> &gt; 1 keeps near-open spots
    /// near full speed and only cuts hard once mostly enclosed.
    ///
    /// Both the room BFS and the light lookup touch chunk data, so skip both entirely outside loaded
    /// terrain (fully open) — otherwise the world-map wind overlay pays this cost every sampled cell at
    /// high zoom-out, most of which land in unloaded chunks it can't usefully occlude anyway
    /// (<see cref="DynamicWind.Client.MapLayers.WindArrowMapLayer"/> intentionally samples everywhere via <c>RequireChunkLoaded = false</c>).
    /// </summary>
    private double OcclusionFactor(Vec3d pos)
    {
        BlockPos bp = new((int)pos.X, (int)pos.Y, (int)pos.Z, 0);
        if (Api.World.BlockAccessor.GetChunkAtBlockPos(bp) == null) return 1.0;

        if (Config.OcclusionRoomsZeroWind && roomRegistry != null)
        {
            Room room = roomRegistry.GetRoomForPosition(bp);
            if (room != null && room.ExitCount == 0 && room.SkylightCount + room.NonSkylightCount > 0) return 0.0;
        }

        int sunLight = Api.World.BlockAccessor.GetLightLevel(bp, EnumLightLevelType.OnlySunLight);

        double t = sunLight / (double)Config.OcclusionLightThreshold;
        if (t < 0) t = 0; else if (t > 1) t = 1;

        return Config.OcclusionMinFactor + (1.0 - Config.OcclusionMinFactor) * Math.Pow(t, Config.OcclusionSharpness);
    }

    // Re-entrancy guard: the terrain-thermal term below calls ThermalPressureFunc → TerrainThermal →
    // GetClimateAt, which re-fires OnGetClimate. Without this we'd recurse until the stack blew. On a
    // nested call we skip our shift entirely (the inner climate is only used to derive thermal
    // pressure; it must not carry our temperature delta anyway).
    [ThreadStatic] private static bool inClimateHandler;

    /// <summary>
    /// Pressure-cell air mass → air temperature. Each system carries its own intrinsic warm/cold
    /// character (<see cref="PressureCell.AirMassTemp"/>, set at spawn from birth latitude + season +
    /// jitter) independent of whether it's a low or a high — a cold low and a warm high can both exist,
    /// same as a polar low or a subtropical high in reality. Shifts local temperature up to
    /// <see cref="DynamicWindConfig.FrontTemperatureMax"/> °C toward that air mass's character; the
    /// terrain-thermal field (warm dry land / cool deep water) adds a gentler shift up to
    /// <see cref="DynamicWindConfig.ThermalTemperatureMax"/> °C. Both terms are read from the same
    /// synced <see cref="WindField"/> used everywhere else, so server and client agree. Worldgen base
    /// climate is left untouched — only live ("now"/dated) lookups shift.
    /// </summary>
    protected void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
    {
        if (climate == null || mode == EnumGetClimateMode.WorldGenValues) return;
        if (inClimateHandler) return;
        bool humidityActive = Config.HumidityEnabled && Config.HumidityRainfallBlendWeight > 0f;
        if (Config.FrontTemperatureMax <= 0f && Config.ThermalTemperatureMax <= 0f && !Config.HeatColdWaveEnabled && !humidityActive) return;

        inClimateHandler = true;
        try
        {
            double totalHours = totalDays * Api.World.Calendar.HoursPerDay;

            // Front term: blended air-mass character in [-1, 1] (independent of pressure sign).
            double t = Field.CellTemperatureAt(pos.X, pos.Z, totalHours);
            double frontDelta = Config.FrontTemperatureMax * Clamp1(t);

            // Terrain thermal term: + warm land / − cool water.
            double thermalDelta = 0;
            if (Config.ThermalTemperatureMax > 0f && Field.ThermalPressureFunc != null)
            {
                double thermal = Field.ThermalPressureFunc(pos.X, pos.Z, totalHours);
                thermalDelta = Config.ThermalTemperatureMax * Clamp1(thermal / ThermalNorm);
            }

            // Heat-wave/cold-wave term: a rare, strong, near-stationary high trapping ground air — see
            // PressureCell.IsHeatColdWave/WaveTemperatureDelta and WindFieldSimulation.TrySeedHeatColdWave.
            double waveDelta = Config.HeatColdWaveEnabled ? Field.HeatColdWaveDeltaAt(pos.X, pos.Z, totalHours) : 0.0;

            climate.Temperature += (float)(frontDelta + thermalDelta + waveDelta);

            // Humidity: blend vanilla's own Rainfall toward our terrain/pressure/noise-derived value, so
            // everything that already reads live Rainfall (crop growth, farmland moisture, third-party
            // mods' climate lookups, HUD Clock's Rainfall line) picks up dynamic humidity for free — see
            // WindField.HumidityAt and "Real latitude"/humidity design notes in CLAUDE.md.
            if (humidityActive)
            {
                double humidity = Field.HumidityAt(pos.X, pos.Z, totalHours);
                float w = Config.HumidityRainfallBlendWeight;
                if (w > 1f) w = 1f;
                climate.Rainfall = climate.Rainfall + (float)(w * (humidity - climate.Rainfall));
            }
        }
        finally
        {
            inClimateHandler = false;
        }
    }

    // Normaliser: typical terrain thermals (|TerrainThermal| up to ~2.6) reach ~ThermalTemperatureMax.
    private const double ThermalNorm = 2.0;

    private static double Clamp1(double v) => v < -1 ? -1 : (v > 1 ? 1 : v);
}
