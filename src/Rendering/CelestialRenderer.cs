using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Entities;

namespace VoxelGame.Rendering;

/// <summary>Draws the sun, moon and stars as flat sprites behind the world. Call before the terrain is drawn.</summary>
public sealed class CelestialRenderer
{
    // Premultiplied alpha blending that leaves the destination's alpha channel alone: the scene stores distance there.
    private static readonly BlendState SceneBlend = new()
    {
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.InverseSourceAlpha,
        AlphaSourceBlend = Blend.Zero,
        AlphaDestinationBlend = Blend.One,
        ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Green | ColorWriteChannels.Blue,
    };

    // Adds light on top of what is there (again leaving the distance in alpha alone). Used to push things past white.
    private static readonly BlendState AddScene = new()
    {
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.Zero,
        AlphaDestinationBlend = Blend.One,
        ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Green | ColorWriteChannels.Blue,
    };

    // How many times brighter than white each thing is when the post-processing can use it (bloom, tone mapping).
    private const float SunBrightness = 6f;
    private const float MoonBrightness = 3f;
    private const float StarBrightness = 4f;

    private readonly SpriteBatch _batch;
    private readonly Texture2D _pixel;
    private readonly Vector3[] _stars;

    public CelestialRenderer(SpriteBatch batch, Texture2D pixel)
    {
        _batch = batch;
        _pixel = pixel;

        var rng = new Random(7);
        _stars = new Vector3[200];
        for (int i = 0; i < _stars.Length; i++)
        {
            // Uniform random direction on a sphere.
            float y = (float)(rng.NextDouble() * 2 - 1);
            float a = (float)(rng.NextDouble() * MathHelper.TwoPi);
            float r = MathF.Sqrt(1 - y * y);
            _stars[i] = new Vector3(r * MathF.Cos(a), y, r * MathF.Sin(a));
        }
    }

    /// <param name="hdr">
    /// Draw the sun, moon and stars brighter than white. The picture can't show that directly, so only do it when bloom or
    /// tone mapping will make use of it (otherwise they would just be clipped to white).
    /// </param>
    public void Draw(Camera camera, DayNightCycle sky, bool hdr = false)
    {
        var viewProjection = camera.View * camera.Projection;
        var viewport = _batch.GraphicsDevice.Viewport;

        float night = 1f - sky.Daylight;
        float starAlpha = night * night * 0.9f;
        bool anyStars = night > 0.05f;
        bool sunVisible = Project(sky.SunDirection, camera.Position, viewProjection, viewport, out var sun);
        bool moonVisible = Project(-sky.SunDirection, camera.Position, viewProjection, viewport, out var moon);   // always opposite the sun

        var sunColor = new Color(255, 244, 200);
        var moonColor = new Color(215, 225, 245);

        _batch.Begin(blendState: SceneBlend, samplerState: SamplerState.PointClamp);
        if (anyStars) DrawStars(camera, viewProjection, viewport, Color.White * starAlpha);
        if (sunVisible) DrawSquare(sun, 56, sunColor);
        if (moonVisible) DrawSquare(moon, 40, moonColor);
        _batch.End();

        if (!hdr) return;

        // Go past white by adding the same shapes again, once per whole unit of extra brightness. The moon and stars fade
        // in with the dark, so the moon doesn't glare against a bright daytime sky.
        _batch.Begin(blendState: AddScene, samplerState: SamplerState.PointClamp);
        if (anyStars) AddBrightness(StarBrightness - 1f, f => DrawStars(camera, viewProjection, viewport, Color.White * (starAlpha * f)));
        if (sunVisible) AddBrightness(SunBrightness - 1f, f => DrawSquare(sun, 56, sunColor * f));
        if (moonVisible) AddBrightness((MoonBrightness - 1f) * night * night, f => DrawSquare(moon, 40, moonColor * f));
        _batch.End();
    }

    /// <summary>Calls <paramref name="draw"/> with 1 for each whole unit of <paramref name="extra"/>, then once more with the remainder.</summary>
    private static void AddBrightness(float extra, Action<float> draw)
    {
        for (; extra >= 1f; extra -= 1f) draw(1f);
        if (extra > 0.01f) draw(extra);
    }

    private void DrawStars(Camera camera, Matrix viewProjection, Viewport viewport, Color color)
    {
        foreach (var star in _stars)
            if (Project(star, camera.Position, viewProjection, viewport, out var p))
                _batch.Draw(_pixel, new Rectangle((int)p.X, (int)p.Y, 2, 2), color);
    }

    private void DrawSquare(Vector2 center, int size, Color color) =>
        _batch.Draw(_pixel, new Rectangle((int)center.X - size / 2, (int)center.Y - size / 2, size, size), color);

    /// <summary>Screen position of a direction at infinity; false if it is behind the camera.</summary>
    private static bool Project(Vector3 direction, Vector3 cameraPosition, Matrix viewProjection, Viewport viewport, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(cameraPosition + direction * 100f, 1f), viewProjection);
        screen = Vector2.Zero;
        if (clip.W <= 0.001f) return false;

        screen = new Vector2(
            (clip.X / clip.W * 0.5f + 0.5f) * viewport.Width,
            (1f - (clip.Y / clip.W * 0.5f + 0.5f)) * viewport.Height);
        return true;
    }
}
