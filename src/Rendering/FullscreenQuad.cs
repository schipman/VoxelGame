using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace VoxelGame.Rendering;

/// <summary>A rectangle covering the whole render target, for running a pixel shader over every pixel.</summary>
public sealed class FullscreenQuad
{
    private static readonly VertexPositionTexture[] Vertices =
    {
        new(new Vector3(-1,  1, 0), new Vector2(0, 0)),
        new(new Vector3( 1,  1, 0), new Vector2(1, 0)),
        new(new Vector3(-1, -1, 0), new Vector2(0, 1)),
        new(new Vector3( 1, -1, 0), new Vector2(1, 1)),
    };

    /// <summary>Draws the quad with whichever effect pass has already been applied.</summary>
    public void Draw(GraphicsDevice device) =>
        device.DrawUserPrimitives(PrimitiveType.TriangleStrip, Vertices, 0, 2);
}
