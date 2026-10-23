using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace VoxelGame.Rendering;

/// <summary>
/// Vertex used by the voxel shader. Light holds the baked per-vertex lighting:
/// R = sky light, G = block light, B = ambient occlusion, A = emissive (all 0..1).
/// </summary>
public struct VoxelVertex : IVertexType
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Color Light;

    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0,  VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Color,   VertexElementUsage.Color, 0));

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

    public VoxelVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Color light)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Light = light;
    }
}
