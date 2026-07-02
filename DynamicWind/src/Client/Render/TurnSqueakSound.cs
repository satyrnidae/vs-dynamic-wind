using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace DynamicWind.Client.Render;

/// <summary>
/// Shared by <see cref="WeathervaneRenderer"/> and <see cref="RotorvaneRenderer"/>: plays a one-shot metal
/// squeak whenever the vane's eased yaw starts actively swinging toward a new wind bearing (edge-triggered
/// on "was aligned, now turning" so a steady wind doesn't retrigger it every frame). Each play picks either
/// the first or last second of the clip at random. Volume scales with the delta angle at the moment it
/// triggers — the bigger the swing still ahead of it (so the faster it eases to catch up), the louder, up
/// to full volume at a 180° reversal; a small correction barely creaks.
/// </summary>
internal sealed class TurnSqueakSound
{
    private const float TurnThreshold = 0.03f; // radians; below this counts as "aligned", not turning
    private const float SegmentSeconds = 1f;
    private const float MaxVolume = 1f;
    private static readonly AssetLocation SoundLocation = new("dynamicwind", "sounds/metalcreak.ogg");

    private readonly ICoreClientAPI capi;
    private readonly Vec3d pos;
    private bool wasTurning;

    public TurnSqueakSound(ICoreClientAPI capi, Vec3d pos)
    {
        this.capi = capi;
        this.pos = pos;
    }

    /// <summary>Call once per unpaused frame with the vane's current eased yaw and its target yaw (may be NaN).</summary>
    public void Update(float currentYaw, float target)
    {
        float delta = float.IsNaN(target) ? 0f : GameMath.AngleRadDistance(currentYaw, target);
        bool isTurning = Math.Abs(delta) > TurnThreshold;
        if (isTurning && !wasTurning) Play(Math.Abs(delta));
        wasTurning = isTurning;
    }

    private void Play(float delta)
    {
        float volume = MaxVolume * Math.Min(1f, delta / GameMath.PI);

        ILoadedSound sound = capi.World.LoadSound(new SoundParams
        {
            Location = SoundLocation,
            ShouldLoop = false,
            DisposeOnFinish = false,
            Position = new Vec3f((float)pos.X + 0.5f, (float)pos.Y + 0.5f, (float)pos.Z + 0.5f),
            Volume = volume,
            Range = 16f,
        });

        if (capi.World.Rand.NextDouble() < 0.5)
        {
            sound.PlaybackPosition = Math.Max(0f, sound.SoundLengthSeconds - SegmentSeconds);
        }
        sound.Start();

        capi.Event.RegisterCallback(_ =>
        {
            sound.Stop();
            sound.Dispose();
        }, (int)(SegmentSeconds * 1000));
    }
}
