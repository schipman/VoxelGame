using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;
using VoxelGame.Rendering;

namespace VoxelGame.Terrain;

/// <summary>
/// A 16x128x16 column of blocks plus the GPU mesh used to draw it.
/// Block coordinates passed to Get/Set are local to the chunk.
/// </summary>
public sealed class Chunk : IDisposable
{
    public const int Size = 16;
    public const int Height = 128;

    private readonly BlockType[] _blocks = new BlockType[Size * Height * Size];

    public int ChunkX { get; }
    public int ChunkZ { get; }
    public int WorldX => ChunkX * Size;
    public int WorldZ => ChunkZ * Size;

    /// <summary>True when the player changed blocks since this chunk was last saved (or loaded).</summary>
    public bool NeedsSave { get; set; }

    /// <summary>All blocks in storage order, for saving.</summary>
    public ReadOnlySpan<BlockType> Blocks => _blocks;

    /// <summary>Replaces every block at once, for loading. Must be given Size*Height*Size entries.</summary>
    public void LoadBlocks(ReadOnlySpan<BlockType> blocks)
    {
        blocks.CopyTo(_blocks);
        NeedsMesh = true;
    }

    /// <summary>True when the blocks changed since the mesh was last built.</summary>
    public bool NeedsMesh { get; set; } = true;

    public VertexBuffer VertexBuffer { get; private set; }
    public IndexBuffer IndexBuffer { get; private set; }
    public int IndexCount { get; private set; }
    public int VertexCount { get; private set; }

    public BoundingBox Bounds { get; }

    public Chunk(int chunkX, int chunkZ)
    {
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        Bounds = new BoundingBox(
            new Vector3(WorldX, 0, WorldZ),
            new Vector3(WorldX + Size, Height, WorldZ + Size));
    }

    // Light per voxel: high nibble = sky light (0-15), low nibble = block light (0-15).
    private readonly byte[] _light = new byte[Size * Height * Size];

    private static int Index(int x, int y, int z) => x + z * Size + y * Size * Size;

    public int GetSkyLight(int x, int y, int z) => _light[Index(x, y, z)] >> 4;
    public int GetBlockLight(int x, int y, int z) => _light[Index(x, y, z)] & 15;

    public void SetSkyLight(int x, int y, int z, int level)
    {
        int i = Index(x, y, z);
        _light[i] = (byte)((_light[i] & 0x0F) | (level << 4));
    }

    public void SetBlockLight(int x, int y, int z, int level)
    {
        int i = Index(x, y, z);
        _light[i] = (byte)((_light[i] & 0xF0) | level);
    }

    public BlockType Get(int x, int y, int z) => _blocks[Index(x, y, z)];

    public void Set(int x, int y, int z, BlockType type)
    {
        _blocks[Index(x, y, z)] = type;
        NeedsMesh = true;
    }

    /// <summary>Replaces the GPU buffers with a freshly built mesh.</summary>
    public void SetMesh(GraphicsDevice device, List<VoxelVertex> vertices, List<int> indices)
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
        VertexBuffer = null;
        IndexBuffer = null;
        VertexCount = vertices.Count;
        IndexCount = indices.Count;

        if (indices.Count > 0)
        {
            VertexBuffer = new VertexBuffer(device, VoxelVertex.VertexDeclaration, vertices.Count, BufferUsage.WriteOnly);
            VertexBuffer.SetData(vertices.ToArray());
            IndexBuffer = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, indices.Count, BufferUsage.WriteOnly);
            IndexBuffer.SetData(indices.ToArray());
        }
        NeedsMesh = false;
    }

    public void Dispose()
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
    }
}
