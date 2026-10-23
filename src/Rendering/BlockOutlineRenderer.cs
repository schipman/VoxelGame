using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Entities;
using VoxelGame.Terrain;

namespace VoxelGame.Rendering;

/// <summary>Draws a black wireframe box around the block the player is looking at.</summary>
public sealed class BlockOutlineRenderer : IDisposable
{
    private const float Grow = 0.002f;   // slightly oversized so lines don't z-fight with the block faces

    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    private readonly VertexPositionColor[] _lines = new VertexPositionColor[24];

    public BlockOutlineRenderer(GraphicsDevice device)
    {
        _device = device;
        _effect = new BasicEffect(device) { VertexColorEnabled = true, TextureEnabled = false, LightingEnabled = false };

        // 12 cube edges as 24 line-list vertices.
        var c = new Vector3[8];
        for (int i = 0; i < 8; i++)
            c[i] = new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        int[] edges = { 0,1, 2,3, 4,5, 6,7,  0,2, 1,3, 4,6, 5,7,  0,4, 1,5, 2,6, 3,7 };
        for (int i = 0; i < 24; i++)
            _lines[i] = new VertexPositionColor(c[edges[i]], Color.Black * 0.85f);
    }

    public void Draw(BlockHit hit, Camera camera)
    {
        _effect.World = Matrix.CreateScale(1 + 2 * Grow) * Matrix.CreateTranslation(hit.X - Grow, hit.Y - Grow, hit.Z - Grow);
        _effect.View = camera.View;
        _effect.Projection = camera.Projection;

        _device.DepthStencilState = DepthStencilState.Default;
        _device.BlendState = BlendState.AlphaBlend;
        _device.RasterizerState = RasterizerState.CullNone;

        _effect.CurrentTechnique.Passes[0].Apply();
        _device.DrawUserPrimitives(PrimitiveType.LineList, _lines, 0, 12);
    }

    public void Dispose() => _effect.Dispose();
}
