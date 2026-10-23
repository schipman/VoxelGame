using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;
using VoxelGame.Rendering;

namespace VoxelGame.Terrain;

/// <summary>
/// A 16x128x16 column of blocks plus the GPU meshes used to draw it.
/// Block coordinates passed to Get/Set are local to the chunk.
/// </summary>
public sealed class Chunk : IDisposable
{
    public const int Size = 16;
    public const int Height = 128;

    private readonly BlockType[] _blocks = new BlockType[Size * Height * Size];

    // Light per voxel: high nibble = sky light (0-15), low nibble = block light (0-15).
    private readonly byte[] _light = new byte[Size * Height * Size];

    // Fluid level per voxel, parallel to the block array (see FluidLevels).
    private readonly byte[] _fluid = new byte[Size * Height * Size];

    // Direction a river flows through each column (0 = none), as an angle packed in a byte. Not saved: the
    // generator recomputes it, since rivers are a pure function of the seed.
    private readonly byte[] _flow = new byte[Size * Size];

    public int ChunkX { get; }
    public int ChunkZ { get; }
    public int WorldX => ChunkX * Size;
    public int WorldZ => ChunkZ * Size;

    /// <summary>True when blocks changed since this chunk was last saved (or loaded).</summary>
    public bool NeedsSave { get; set; }

    /// <summary>True when the blocks or light changed since the meshes were last built.</summary>
    public bool NeedsMesh { get; set; } = true;

    /// <summary>The solid terrain and everything that isn't water.</summary>
    public ChunkMesh Mesh { get; } = new();

    /// <summary>Water surfaces, drawn in a separate pass by the water shader.</summary>
    public ChunkMesh WaterMesh { get; } = new();

    public BoundingBox Bounds { get; }

    public Chunk(int chunkX, int chunkZ)
    {
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        Bounds = new BoundingBox(
            new Vector3(WorldX, 0, WorldZ),
            new Vector3(WorldX + Size, Height, WorldZ + Size));
    }

    private static int Index(int x, int y, int z) => x + z * Size + y * Size * Size;

    // ---- Blocks ----

    public BlockType Get(int x, int y, int z) => _blocks[Index(x, y, z)];

    public void Set(int x, int y, int z, BlockType type)
    {
        _blocks[Index(x, y, z)] = type;
        NeedsMesh = true;
    }

    /// <summary>All blocks in storage order, for saving.</summary>
    public ReadOnlySpan<BlockType> Blocks => _blocks;

    /// <summary>Replaces every block at once, for loading. Must be given Size*Height*Size entries.</summary>
    public void LoadBlocks(ReadOnlySpan<BlockType> blocks)
    {
        blocks.CopyTo(_blocks);
        NeedsMesh = true;
    }

    // ---- Light ----

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

    // ---- Fluids ----

    /// <summary>
    /// Fluid level of each voxel: 0 = a source block (full, never drains), 1-7 = flowing water, higher means
    /// thinner and further from its source. Meaningless for blocks that aren't liquid.
    /// </summary>
    public ReadOnlySpan<byte> FluidLevels => _fluid;

    public void LoadFluidLevels(ReadOnlySpan<byte> levels) => levels.CopyTo(_fluid);

    public int GetFluidLevel(int x, int y, int z) => _fluid[Index(x, y, z)];
    public void SetFluidLevel(int x, int y, int z, int level) => _fluid[Index(x, y, z)] = (byte)level;

    /// <summary>Direction the river in this column flows, as a unit vector; zero if there is no river.</summary>
    public Vector2 GetFlow(int x, int z)
    {
        byte code = _flow[x + z * Size];
        if (code == 0) return Vector2.Zero;
        float angle = (code - 1) / 254f * MathHelper.TwoPi;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    public void SetFlow(int x, int z, Vector2 direction)
    {
        if (direction.LengthSquared() < 1e-6f) { _flow[x + z * Size] = 0; return; }
        float angle = MathF.Atan2(direction.Y, direction.X);
        if (angle < 0) angle += MathHelper.TwoPi;
        _flow[x + z * Size] = (byte)(1 + (int)MathF.Round(angle / MathHelper.TwoPi * 254f));
    }

    // ---- Meshes ----

    /// <summary>Replaces the GPU buffers of both meshes with freshly built ones.</summary>
    public void SetMeshes(GraphicsDevice device,
        List<VoxelVertex> vertices, List<int> indices,
        List<VoxelVertex> waterVertices, List<int> waterIndices)
    {
        Mesh.Set(device, vertices, indices);
        WaterMesh.Set(device, waterVertices, waterIndices);
        NeedsMesh = false;
    }

    public void Dispose()
    {
        Mesh.Dispose();
        WaterMesh.Dispose();
    }
}

/// <summary>The GPU buffers for one batch of triangles.</summary>
public sealed class ChunkMesh : IDisposable
{
    public VertexBuffer VertexBuffer { get; private set; }
    public IndexBuffer IndexBuffer { get; private set; }
    public int IndexCount { get; private set; }
    public int VertexCount { get; private set; }

    public void Set(GraphicsDevice device, List<VoxelVertex> vertices, List<int> indices)
    {
        Dispose();
        VertexCount = vertices.Count;
        IndexCount = indices.Count;
        if (indices.Count == 0) return;

        VertexBuffer = new VertexBuffer(device, VoxelVertex.VertexDeclaration, vertices.Count, BufferUsage.WriteOnly);
        VertexBuffer.SetData(vertices.ToArray());
        IndexBuffer = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, indices.Count, BufferUsage.WriteOnly);
        IndexBuffer.SetData(indices.ToArray());
    }

    public void Dispose()
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
        VertexBuffer = null;
        IndexBuffer = null;
        IndexCount = 0;
    }
}
