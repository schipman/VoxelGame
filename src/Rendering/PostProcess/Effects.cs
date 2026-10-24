using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Entities;

namespace VoxelGame.Rendering.PostProcess;

/// <summary>
/// Makes bright things glow. The scene is shrunk through a chain of ever blurrier copies (keeping only what is brighter
/// than <see cref="Threshold"/>), the copies are blended back together from the blurriest up, and the result is added
/// to the picture. Wide, soft glow is cheap this way because the blurry copies are tiny.
/// </summary>
public sealed class BloomEffect : PostEffect
{
    private const int MaxLevels = 6;
    private const int SmallestLevel = 16;   // pixels on the short side

    /// <summary>Brightness (1 = white) above which things start to glow.</summary>
    public float Threshold = 0.75f;

    /// <summary>How gradual the start of the glow is.</summary>
    public float Knee = 0.35f;

    /// <summary>How much glow is added to the picture. 0 turns the effect off.</summary>
    public float Intensity = 0.4f;

    /// <summary>How wide the glow spreads (0..1); higher mixes in more of the blurriest copies.</summary>
    public float Scatter = 0.7f;

    private RenderTarget2D[] _down = Array.Empty<RenderTarget2D>();   // progressively smaller
    private RenderTarget2D[] _up = Array.Empty<RenderTarget2D>();     // the same sizes, with the blurrier ones mixed back in

    public override bool IsActive => Enabled && Intensity > 0.001f;

    public override void Apply(PostContext c, Texture2D source, RenderTarget2D destination)
    {
        EnsureLevels(c.Device, c.Width, c.Height);

        c.Set("Threshold", Threshold);
        c.Set("Knee", Knee);
        c.Set("Intensity", Intensity);
        c.Set("Scatter", Scatter);

        c.Pass(_down[0], "BloomPrefilter", source);
        for (int i = 1; i < _down.Length; i++)
            c.Pass(_down[i], "BloomDownsample", _down[i - 1]);

        Texture2D glow = _down[^1];
        for (int i = _down.Length - 2; i >= 0; i--)
        {
            c.Pass(_up[i], "BloomUpsample", glow, _down[i]);
            glow = _up[i];
        }

        c.Pass(destination, "BloomComposite", source, glow);
    }

    private void EnsureLevels(GraphicsDevice device, int width, int height)
    {
        int w = Math.Max(width / 2, 1), h = Math.Max(height / 2, 1);
        if (_down.Length > 0 && _down[0].Width == w && _down[0].Height == h) return;
        DisposeLevels();

        var sizes = new List<(int, int)>();
        while (sizes.Count < MaxLevels && Math.Min(w, h) >= SmallestLevel)
        {
            sizes.Add((w, h));
            w = Math.Max(w / 2, 1);
            h = Math.Max(h / 2, 1);
        }
        if (sizes.Count == 0) sizes.Add((Math.Max(width / 2, 1), Math.Max(height / 2, 1)));

        _down = sizes.Select(s => new RenderTarget2D(device, s.Item1, s.Item2, false, SurfaceFormat.HalfVector4, DepthFormat.None)).ToArray();
        _up = sizes.Select(s => new RenderTarget2D(device, s.Item1, s.Item2, false, SurfaceFormat.HalfVector4, DepthFormat.None)).ToArray();
    }

    private void DisposeLevels()
    {
        foreach (var t in _down) t.Dispose();
        foreach (var t in _up) t.Dispose();
        _down = _up = Array.Empty<RenderTarget2D>();
    }

    public override void Dispose() => DisposeLevels();
}

/// <summary>
/// Light shafts ("god rays") fanning out from the sun and moon around hills and trees. Works on the scene itself: the
/// sun and moon are drawn brighter than white, and sky pixels are recognisable by their huge distance (see Post.fx), so
/// no extra render pass is needed. The shafts are made at half resolution and added to the picture.
/// </summary>
public sealed class GodRaysEffect : PostEffect
{
    /// <summary>How much shaft light is added. 0 turns the effect off.</summary>
    public float Intensity = 0.5f;

    /// <summary>How much each step toward the light dims what it carries; closer to 1 gives longer shafts.</summary>
    public float Decay = 0.988f;

    /// <summary>Set by the renderer while the camera is under water, where there is no sky to see.</summary>
    public bool Suppressed;

    private RenderTarget2D _shafts;
    private Vector2 _sunUv, _moonUv;
    private float _sunStrength, _moonStrength;

    /// <summary>Whether the effect is switched on at all (as opposed to merely having nothing to show this frame).</summary>
    public bool Wanted => Enabled && Intensity > 0.001f;

    public override bool IsActive => Wanted && !Suppressed && (_sunStrength > 0.01f || _moonStrength > 0.01f);

    /// <summary>Works out where the sun and moon are on screen and how strongly each shines. Call once a frame, before the stack runs.</summary>
    public void Aim(Camera camera, DayNightCycle sky)
    {
        var viewProjection = camera.View * camera.Projection;
        float sunHeight = sky.SunDirection.Y;

        // The sun is strongest low in the sky (long golden shafts) and gone once it sets; the moon is fainter.
        float sunUp = SmoothStep(-0.1f, 0.08f, sunHeight);
        float low = 1f + 0.8f * (1f - MathHelper.Clamp(sunHeight * 3f, 0f, 1f));
        _sunStrength = Aim(camera, viewProjection, sky.SunDirection, sunUp * low, out _sunUv);
        _moonStrength = Aim(camera, viewProjection, -sky.SunDirection, SmoothStep(-0.1f, 0.08f, -sunHeight) * 0.6f, out _moonUv);
    }

    private static float Aim(Camera camera, Matrix viewProjection, Vector3 direction, float strength, out Vector2 uv)
    {
        uv = Vector2.Zero;
        var clip = Vector4.Transform(new Vector4(camera.Position + direction * 100f, 1f), viewProjection);
        if (clip.W <= 0.001f || strength <= 0f) return 0f;

        uv = new Vector2(clip.X / clip.W * 0.5f + 0.5f, 0.5f - clip.Y / clip.W * 0.5f);

        // Fade out as we turn away from the light, so shafts don't linger once it is well off to the side.
        float facing = Vector3.Dot(camera.Forward, direction);
        return strength * SmoothStep(-0.1f, 0.45f, facing);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = MathHelper.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public override void Apply(PostContext c, Texture2D source, RenderTarget2D destination)
    {
        int w = Math.Max(c.Width / 2, 1), h = Math.Max(c.Height / 2, 1);
        if (_shafts == null || _shafts.Width != w || _shafts.Height != h)
        {
            _shafts?.Dispose();
            _shafts = new RenderTarget2D(c.Device, w, h, false, SurfaceFormat.HalfVector4, DepthFormat.None);
        }

        c.Set("SunUv", _sunUv);
        c.Set("MoonUv", _moonUv);
        c.Set("SunStrength", _sunStrength);
        c.Set("MoonStrength", _moonStrength);
        c.Set("RayThreshold", 1.2f);
        c.Set("RayDecay", Decay);
        c.Pass(_shafts, "GodRays", source);

        c.Set("Intensity", Intensity);
        c.Pass(destination, "BloomComposite", source, _shafts);   // scene + shafts * Intensity
    }

    public override void Dispose() => _shafts?.Dispose();
}

/// <summary>Darkens the corners of the screen, which draws the eye to the middle.</summary>
public sealed class VignetteEffect : PostEffect
{
    /// <summary>0 = none, 1 = the corners go fully black.</summary>
    public float Strength = 0.35f;

    /// <summary>Distance from the centre where the darkening starts (the screen's short side counts as 1 from centre to edge).</summary>
    public float Radius = 0.7f;

    /// <summary>How far beyond the radius it takes to reach full strength.</summary>
    public float Softness = 0.9f;

    /// <summary>Extra strength on top of <see cref="Strength"/> for the moment, e.g. while under water.</summary>
    public float Boost;

    public override bool IsActive => Enabled && Strength + Boost > 0.001f;

    public override void Apply(PostContext c, Texture2D source, RenderTarget2D destination)
    {
        c.Set("VignetteStrength", Math.Clamp(Strength + Boost, 0f, 1f));
        c.Set("VignetteRadius", Radius);
        c.Set("VignetteSoftness", Softness);
        c.Set("Aspect", c.Width / (float)c.Height);
        c.Pass(destination, "Vignette", source);
    }
}

/// <summary>
/// AgX tone mapping: compresses the scene's unclamped brightness into what a screen can show, with a filmic curve that
/// keeps detail in highlights and shadows and rolls bright colours off toward white. It changes the whole look
/// (flatter and softer than the raw image), so brightness is adjustable and a punchier variant is available.
/// </summary>
public sealed class AgxTonemapEffect : PostEffect
{
    /// <summary>Brightness multiplier going in; AgX is fairly dark and flat, so a little over 1 often suits.</summary>
    public float Exposure = 1f;

    /// <summary>How much extra contrast and colour to add to AgX's flat default look (0 = none, 1 = a lot).</summary>
    public float Punch = 0.35f;

    public AgxTonemapEffect() { }

    public override void Apply(PostContext c, Texture2D source, RenderTarget2D destination)
    {
        c.Set("TonemapExposure", Exposure);
        c.Set("Punchy", Punch);
        c.Pass(destination, "AgX", source);
    }
}

/// <summary>Exposure, contrast and colour saturation. With the defaults it changes nothing and is skipped.</summary>
public sealed class ColorGradeEffect : PostEffect
{
    public float Exposure = 1f;
    public float Contrast = 1f;

    /// <summary>1 = as is, 0 = black and white, above 1 = more vivid.</summary>
    public float Saturation = 1f;

    public override bool IsActive =>
        Enabled && (MathF.Abs(Exposure - 1f) > 0.001f || MathF.Abs(Contrast - 1f) > 0.001f || MathF.Abs(Saturation - 1f) > 0.001f);

    public override void Apply(PostContext c, Texture2D source, RenderTarget2D destination)
    {
        c.Set("Exposure", Exposure);
        c.Set("Contrast", Contrast);
        c.Set("Saturation", Saturation);
        c.Pass(destination, "Grade", source);
    }
}
