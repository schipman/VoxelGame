using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Entities;

namespace VoxelGame.Rendering;

/// <summary>Draws the sun, moon and stars as flat sprites behind the world. Call before the terrain is drawn.</summary>
public sealed class CelestialRenderer
{
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

    public void Draw(Camera camera, DayNightCycle sky)
    {
        var viewProjection = camera.View * camera.Projection;
        var viewport = _batch.GraphicsDevice.Viewport;

        _batch.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.PointClamp);

        float night = 1f - sky.Daylight;
        if (night > 0.05f)
        {
            foreach (var star in _stars)
                if (Project(star, camera.Position, viewProjection, viewport, out var p))
                    _batch.Draw(_pixel, new Rectangle((int)p.X, (int)p.Y, 2, 2), Color.White * (night * night * 0.9f));
        }

        // Sun is always opposite the moon.
        if (Project(sky.SunDirection, camera.Position, viewProjection, viewport, out var sun))
            DrawSquare(sun, 56, new Color(255, 244, 200));
        if (Project(-sky.SunDirection, camera.Position, viewProjection, viewport, out var moon))
            DrawSquare(moon, 40, new Color(215, 225, 245));

        _batch.End();
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
