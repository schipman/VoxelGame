using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace VoxelGame.Rendering;

/// <summary>
/// Vertex used by the voxel and water shaders. Both Color fields hold values in 0..1.
///   Light: R = sky light, G = block light, B = ambient occlusion, A = emissive (lamp faces).
///   Extra (solid terrain): R = how deep under water the face is (blocks / 15), G = sky light at the water's surface.
///   Extra (water):         R = 1 on the water's top surface (waves move these vertices),
///                          G,B = direction the water flows (x,z; 0.5 = still).
/// </summary>
public struct VoxelVertex : IVertexType
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Color Light;
    public Color Extra;

    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0,  VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Color,   VertexElementUsage.Color, 0),
        new VertexElement(36, VertexElementFormat.Color,   VertexElementUsage.Color, 1));

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;

    public VoxelVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Color light, Color extra = default)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Light = light;
        Extra = extra;
    }
}
