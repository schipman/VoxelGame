using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Client;
using VoxelGame.Entities;

namespace VoxelGame.Rendering;

/// <summary>Draws the other players as simple blocky figures: a head that looks where they look, a body, arms and legs.</summary>
public sealed class PlayerAvatarRenderer : IDisposable
{
    private static readonly Color[] Shirts =
    {
        new(200, 70, 70), new(70, 120, 210), new(80, 170, 90), new(220, 170, 50),
        new(160, 90, 200), new(60, 180, 190), new(230, 120, 60), new(200, 100, 160),
    };

    private static readonly Color Skin = new(224, 180, 140);
    private static readonly Color Trousers = new(60, 62, 100);

    // Brightness of each face, so the boxes read as solid shapes without any lighting.
    private static readonly float[] FaceShade = { 0.85f, 0.85f, 1.0f, 0.55f, 0.7f, 0.9f };   // -Z (front), +Z, top, bottom, -X, +X

    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    private readonly VertexPositionColor[] _box = new VertexPositionColor[36];

    public PlayerAvatarRenderer(GraphicsDevice device)
    {
        _device = device;
        _effect = new BasicEffect(device) { VertexColorEnabled = true, TextureEnabled = false, LightingEnabled = false };
    }

    public static Color ShirtFor(int playerId) => Shirts[Math.Abs(playerId) % Shirts.Length];

    /// <summary>Draws everyone. Call after the terrain and before the water, so water in front of a player tints them.</summary>
    public void Draw(IEnumerable<RemotePlayer> players, Camera camera)
    {
        _effect.View = camera.View;
        _effect.Projection = camera.Projection;
        _device.DepthStencilState = DepthStencilState.Default;
        _device.BlendState = BlendState.Opaque;
        _device.RasterizerState = RasterizerState.CullCounterClockwise;

        foreach (var p in players)
        {
            if (!p.Visible) continue;
            var body = Matrix.CreateRotationY(p.Yaw) * Matrix.CreateTranslation(p.Position);
            var shirt = ShirtFor(p.Id);

            Part(body, new Vector3(-0.25f, 0.00f, -0.12f), new Vector3(0.25f, 0.75f, 0.12f), Trousers);   // legs (one block with a seam is plenty at this size)
            Part(body, new Vector3(-0.25f, 0.75f, -0.14f), new Vector3(0.25f, 1.40f, 0.14f), shirt);       // torso
            Part(body, new Vector3(-0.45f, 0.78f, -0.11f), new Vector3(-0.25f, 1.40f, 0.11f), shirt);      // arms
            Part(body, new Vector3(0.25f, 0.78f, -0.11f), new Vector3(0.45f, 1.40f, 0.11f), shirt);

            // The head turns up and down about the neck.
            var head = Matrix.CreateTranslation(0, -1.40f, 0) * Matrix.CreateRotationX(p.Pitch) * Matrix.CreateTranslation(0, 1.40f, 0) * body;
            Part(head, new Vector3(-0.25f, 1.40f, -0.25f), new Vector3(0.25f, 1.90f, 0.25f), Skin);
            Part(head, new Vector3(-0.17f, 1.60f, -0.255f), new Vector3(-0.07f, 1.68f, -0.24f), new Color(40, 40, 60));   // eyes, on the front face
            Part(head, new Vector3(0.07f, 1.60f, -0.255f), new Vector3(0.17f, 1.68f, -0.24f), new Color(40, 40, 60));
        }
    }

    /// <summary>Where a player's name tag goes: just above the head.</summary>
    public static Vector3 TagPosition(RemotePlayer p) => p.Position + new Vector3(0, 2.15f, 0);

    private void Part(Matrix world, Vector3 min, Vector3 max, Color color)
    {
        int v = 0;
        void Face(int face, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var shaded = new Color((int)(color.R * FaceShade[face]), (int)(color.G * FaceShade[face]), (int)(color.B * FaceShade[face]));
            // MonoGame treats clockwise triangles as front-facing, so the corners go out in the opposite order to how they are listed below.
            _box[v++] = new VertexPositionColor(a, shaded); _box[v++] = new VertexPositionColor(c, shaded); _box[v++] = new VertexPositionColor(b, shaded);
            _box[v++] = new VertexPositionColor(a, shaded); _box[v++] = new VertexPositionColor(d, shaded); _box[v++] = new VertexPositionColor(c, shaded);
        }

        float x0 = min.X, y0 = min.Y, z0 = min.Z, x1 = max.X, y1 = max.Y, z1 = max.Z;
        // Counter-clockwise when seen from outside.
        Face(0, new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0));   // front (-Z)
        Face(1, new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));   // back (+Z)
        Face(2, new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0));   // top
        Face(3, new(x0, y0, z1), new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1));   // bottom
        Face(4, new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0));   // -X
        Face(5, new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1));   // +X

        _effect.World = world;
        _effect.CurrentTechnique.Passes[0].Apply();
        _device.DrawUserPrimitives(PrimitiveType.TriangleList, _box, 0, 12);
    }

    public void Dispose() => _effect.Dispose();
}
