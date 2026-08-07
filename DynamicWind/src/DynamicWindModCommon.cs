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

    // Front-type gust intensity: occluded fronts bring the highest winds, cold fronts heavy gusts, warm
    // fronts light gusts, stationary fronts stay comparatively calm - same regime split as
    // WeatherCoupling's precipitation weighting, applied as a multiplier on front-driven EXCESS wind only
    // (see ComputeWindSpeed), never on the vanilla baseline.
    private const double OccludedWindMult = 1.6;
    private const double ColdWindMult = 1.3;
    private const double WarmWindMult = 0.6;
    private const double StationaryWindMult = 0.3;

    // Former DynamicWindConfig tunables, now fixed constants (see DynamicWindConfig's class remarks).
    private const double FrontWindStrength = 0.4;
    private const float FrontTemperatureMax = 3.0f;
    private const float ThermalTemperatureMax = 1.0f;
    private const float HumidityRainfallBlendWeight = 0.7f;
    private const double OcclusionLightThreshold = 10.0;
    private const double OcclusionSharpness = 3.0;
    private const double OcclusionMinFactor = 0.0;
    private const float SeasonalBiasMaxAngleDeg = 60f;

    public override bool ShouldLoad(EnumAppSide side) => false;

    // Load after the vanilla weather system so OnGetWindSpeed runs after it (vanilla sets windSpeed.X to
    // the scalar strength, which we read as magnitude and redirect).
    public override double ExecuteOrder() => 1.0;

    #region Setup

    public override void Start(ICoreAPI api)
    {
        Api = api;
        LoadConfig(api);
        ApplyConfigToField();
        roomRegistry = api.ModLoader.GetModSystem<RoomRegistry>();

        // NOTE: message types must be registered in the SAME ORDER on both sides - the channel's version
        // check compares the ordered type list, so appending here (never inserting) is what keeps client
        // and server compatible.
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

    /// <summary>Wires <see cref="WindField.TerrainTempFunc"/> to a cached lookup of
    /// <see cref="TerrainThermal.TemperatureAnomalyAt"/>. Shared by both sides; terrain-height caching still
    /// differs per side and is set up separately.</summary>
    protected void SetupTerrainTempFunc(ICoreAPI api)
    {
        var thermalCache = new ThermalCache((x, z, th) => TerrainThermal.TemperatureAnomalyAt(api.World, x, z, th));
        Field.TerrainTempFunc = thermalCache.Get;
    }

    /// <summary>Wires <see cref="WindField.TerrainHumidityFunc"/> to a cached lookup of <see cref="TerrainHumidity.WorldgenBaselineAt"/>.
    /// Shared by both sides - the worldgen rainfall map is stable, so a coarse tile + effectively-static
    /// time bucket (mirrors the terrain-height caches) keeps this cheap on the wind hot path.</summary>
    protected void SetupTerrainHumidityFunc(ICoreAPI api)
    {
        var humidityCache = new ThermalCache((x, z, _) => TerrainHumidity.WorldgenBaselineAt(api.World, x, z), tile: 32.0, bucketHours: 1e9);
        Field.TerrainHumidityFunc = humidityCache.Get;
    }

    /// <summary>Sets <see cref="WindField.SeaLevel"/>/<see cref="WindField.WorldHeight"/>/<see cref="WindField.MapCenterZ"/>
    /// and wires <see cref="WindField.LatitudeFunc"/> to the real climate latitude (repeating triangle wave -
    /// see "Real latitude" in CLAUDE.md). Re-checked per call rather than captured once, since
    /// <c>Calendar.OnGetLatitude</c> is wired up by world-gen and may not be assigned yet. Falls back to the
    /// old map-midline heuristic until it is. Shared by both sides; call once world geometry is available
    /// (server: <c>SaveGameLoaded</c>, client: <c>LevelFinalize</c>).</summary>
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
        Field.SeasonalBiasMaxAngle = Config.SeasonalBias ? SeasonalBiasMaxAngleDeg * GameMath.DEG2RAD : 0.0;
        Field.OrographicEnabled = Config.OrographicEnabled;
    }

    #endregion

    #region Wind speed hook

    /// <summary>
    /// Directional wind hook. Vanilla's weather system runs first and stores its own scalar wind speed
    /// (SimplexNoise over time, not diurnal) in <c>windSpeed.X</c>; we keep that as the baseline jetstream
    /// speed and point it along the true direction from <see cref="WindField.WindAt"/>. Pressure CELLS
    /// (fronts/highs/lows - not the ever-present terrain-thermal sea-breeze gradient, which still steers
    /// direction but is deliberately excluded from this excess, see <see cref="ComputeWindSpeed"/>) then
    /// ADD excess above the calm baseline - additive, not multiplicative, so a calm vanilla pattern can't
    /// zero the wind globally, fronts still blow when vanilla is calm, and cell-free areas fall back to
    /// vanilla's own (possibly near-zero) speed. Every <c>GetWindSpeedAt</c> consumer (particles, foliage,
    /// blowing snow, dropped items, ground-movement) follows it for free.
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
    /// need the true wind speed but can't (or shouldn't) pay for the per-position occlusion check - e.g.
    /// <see cref="Client.MapLayers.WindArrowMapLayer"/>, which samples a 24x24 world-map grid where
    /// "indoors" is meaningless and the room/light lookups would be the dominant cost - can pass
    /// <paramref name="applyOcclusion"/> = false and get the same speed/direction sans that factor.
    /// <paramref name="vsSpeed"/> is vanilla's own raw wind-speed noise at <paramref name="pos"/> (e.g.
    /// <c>WeatherSystemBase.WeatherDataSlowAccess.GetWindSpeed(pos)</c>), taken as a parameter so callers
    /// outside the hook chain (which never receive vanilla's pre-computed value) can supply it directly.
    /// </summary>
    public Vec3d ComputeWindSpeed(Vec3d pos, double vsSpeed, double totalHours, bool applyOcclusion)
    {
        // One pass yields both the full wind vector (direction) and the cells-only magnitude used for the
        // front excess below - they differ only in the down-gradient term's pressure source, so evaluating
        // WindAt twice re-walked the cell list and re-ran the orographic step for nothing on this very hot
        // path (every GetWindSpeedAt: per dropped item, particle, foliage sample and windmill tick).
        Vec2d w = Field.WindAt(pos.X, pos.Z, totalHours, out double oroFactor, out double preOroCellMag);

        double mag = w.Length();
        if (mag < 1e-6) return new Vec3d(vsSpeed, 0, 0); // degenerate field -> leave vanilla value

        // Front-driven excess is measured from pressure CELLS only (excludeTerrainGradient). The full
        // field above (used for direction/bearing) also includes the ever-present sea-breeze term
        // (WindField's gradient of TerrainThermal's temperature anomaly) - real and legitimately steers
        // wind near any land/water/terrain contrast, but isn't a "front": folding it into frontExcess
        // would inflate speed near every coastline even with zero active pressure cells nearby.
        // (preOroCellMag comes from the single WindAt call above - see its remarks.)

        // Baseline jetstream speed = vanilla's, modulated by a slow jet-streak noise (faster/slower
        // segments, never zero). Pressure fronts then ADD wind on top of that. Orographic interaction
        // (windward climb, lee wake) then scales the WHOLE total down, so terrain slows baseline wind
        // too, not just front-driven excess.
        double frontExcess = preOroCellMag - Field.BaseStrength;
        if (frontExcess < 0) frontExcess = 0;

        // Weight that excess by which front regime(s) influence this position - see the *WindMult
        // consts above. Falls back to a neutral 1x where no low's influence reaches (plain jetstream/
        // high-pressure gradient wind, which isn't a "front" and shouldn't be gust-differentiated).
        WindField.FrontRegimeBias regime = Field.FrontRegimeBiasAt(pos.X, pos.Z, totalHours);
        double regimeTotal = regime.Cold + regime.Warm + regime.Occluded + regime.Stationary;
        double frontTypeWindMult = regimeTotal > 1e-6
            ? (regime.Occluded * OccludedWindMult + regime.Cold * ColdWindMult
               + regime.Warm * WarmWindMult + regime.Stationary * StationaryWindMult) / regimeTotal
            : 1.0;

        double speed = (vsSpeed * Field.JetStreak(pos.X, pos.Z, totalHours) + FrontWindStrength * frontExcess * frontTypeWindMult) * oroFactor;

        if (applyOcclusion && Config.OcclusionEnabled && !IsAmbientPlayerQuery(pos)) speed *= OcclusionFactor(pos);

        return new Vec3d(speed * w.X / mag, 0, speed * w.Y / mag);
    }

    /// <summary>
    /// True if <paramref name="pos"/> is (X/Z-wise) the client player's own position. Vanilla's
    /// <c>WeatherSystemClient.OnRenderFrame</c> calls <c>GetWindSpeedAt</c> at the player's exact X/Z every
    /// frame purely to smooth <c>GlobalConstants.CurrentWindSpeedClient</c>/<c>CurrentSurfaceWindSpeedClient</c>
    /// - a single value vanilla then broadcasts world-wide for e.g. the foliage-sway shader uniform, which
    /// is NOT re-queried per leaf/block. Occluding that call would freeze foliage sway for the entire
    /// visible world just because the player stands in one shed. Real per-position callers (dropped items,
    /// particles, weathervanes) essentially never pass the player's exact X/Z, so this exemption doesn't
    /// leak into legitimate occlusion elsewhere.
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
    /// (<see cref="RoomRegistry"/> - same detection cellars/greenhouses use, <c>ExitCount == 0</c> meaning
    /// no gaps to open air) gets flat-zero wind: a glass-roofed greenhouse still reads bright on
    /// <c>OnlySunLight</c> (so the light falloff below would miss it) but has no opening for air to move
    /// through. Elsewhere falls back to a power-curve falloff on sun-exposure light level (0 = fully
    /// enclosed, <see cref="OcclusionLightThreshold"/>+ = fully open); <see cref="OcclusionSharpness"/>
    /// &gt; 1 keeps near-open spots near full speed and only cuts hard once mostly enclosed.
    ///
    /// Both the room BFS and the light lookup touch chunk data, so skip both outside loaded terrain
    /// (treat as fully open) - otherwise the world-map wind overlay pays this cost per sampled cell at
    /// high zoom-out, mostly landing in unloaded chunks it can't usefully occlude anyway
    /// (<see cref="DynamicWind.Client.MapLayers.WindArrowMapLayer"/> samples everywhere via <c>RequireChunkLoaded = false</c>).
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

        double t = sunLight / OcclusionLightThreshold;
        if (t < 0) t = 0; else if (t > 1) t = 1;

        return OcclusionMinFactor + (1.0 - OcclusionMinFactor) * Math.Pow(t, OcclusionSharpness);
    }

    #endregion

    #region Climate hook

    // Re-entrancy guard: the terrain-thermal term below calls TerrainTempFunc -> TerrainThermal ->
    // GetClimateAt, which re-fires OnGetClimate. Without this we'd recurse until the stack blew. On a
    // nested call we skip our shift entirely (the inner climate is only used to derive the terrain
    // temperature anomaly; it must not carry our temperature delta anyway).
    [ThreadStatic] private static bool inClimateHandler;

    /// <summary>
    /// Pressure-cell air mass -> air temperature. Each system carries its own intrinsic warm/cold
    /// character (<see cref="PressureCell.AirMassTemp"/>, set at spawn from birth latitude + season +
    /// jitter) independent of whether it's a low or a high - a cold low and a warm high can both exist,
    /// same as a polar low or a subtropical high in reality. Shifts local temperature up to
    /// <see cref="FrontTemperatureMax"/> deg C toward that air mass's character; the
    /// terrain-thermal field (warm dry land / cool deep water) adds a gentler shift up to
    /// <see cref="ThermalTemperatureMax"/> deg C. Both terms are read from the same
    /// synced <see cref="WindField"/> used everywhere else, so server and client agree. Worldgen base
    /// climate is left untouched - only live ("now"/dated) lookups shift.
    /// </summary>
    protected void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
    {
        if (climate == null || mode == EnumGetClimateMode.WorldGenValues) return;
        if (inClimateHandler) return;
        bool humidityActive = Config.HumidityEnabled;

        inClimateHandler = true;
        try
        {
            double totalHours = totalDays * Api.World.Calendar.HoursPerDay;

            // Front term: blended air-mass character in [-1, 1] (independent of pressure sign).
            double t = Field.CellTemperatureAt(pos.X, pos.Z, totalHours);
            double frontDelta = FrontTemperatureMax * Clamp1(t);

            // Terrain thermal term: + warm land / - cool water. TerrainThermal is already denominated in
            // deg C, so this only normalises its nominal amplitude to the configured maximum shift.
            double thermalDelta = 0;
            if (Field.TerrainTempFunc != null)
            {
                double anomaly = Field.TerrainTempFunc(pos.X, pos.Z, totalHours);
                thermalDelta = ThermalTemperatureMax * Clamp1(anomaly / TerrainThermal.Amplitude);
            }

            climate.Temperature += (float)(frontDelta + thermalDelta);

            // Humidity: blend vanilla's own Rainfall toward our terrain/pressure/noise-derived value, so
            // everything that already reads live Rainfall (crop growth, farmland moisture, third-party
            // mods' climate lookups, HUD Clock's Rainfall line) picks up dynamic humidity for free - see
            // WindField.HumidityAt and "Real latitude"/humidity design notes in CLAUDE.md.
            if (humidityActive)
            {
                double humidity = Field.HumidityAt(pos.X, pos.Z, totalHours);
                climate.Rainfall = climate.Rainfall + (float)(HumidityRainfallBlendWeight * (humidity - climate.Rainfall));
            }
        }
        finally
        {
            inClimateHandler = false;
        }
    }

    private static double Clamp1(double v) => v < -1 ? -1 : (v > 1 ? 1 : v);

    #endregion
}
