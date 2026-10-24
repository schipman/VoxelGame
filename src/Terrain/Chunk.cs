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

    /// <summary>
    /// Counts every change to the blocks or light. A mesh remembers the version it was built from, so a mesh built on a
    /// worker thread can never wrongly mark a chunk as up to date if it was edited while the mesh was being made.
    /// Only ever changed on the main thread.
    /// </summary>
    public int Version { get; private set; } = 1;

    /// <summary>The version the current meshes were built from (0 = never meshed).</summary>
    public int MeshedVersion { get; private set; }

    /// <summary>True when the blocks or light changed since the meshes were last built.</summary>
    public bool NeedsMesh => MeshedVersion != Version;

    /// <summary>True while a worker thread is building a mesh for this chunk.</summary>
    public bool MeshInFlight { get; set; }

    /// <summary>Flags the meshes as out of date.</summary>
    public void MarkDirty() => Version++;

    /// <summary>
    /// One more than the highest non-air block's height: everything above is empty sky. The mesher stops there instead of
    /// scanning all 128 layers. It only ever grows (breaking a block never lowers it), which is safe: it is just an upper bound.
    /// </summary>
    public int TopY { get; private set; }

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
        if (type != BlockType.Air && y >= TopY) TopY = y + 1;
        Version++;
    }

    /// <summary>All blocks in storage order, for saving.</summary>
    public ReadOnlySpan<BlockType> Blocks => _blocks;

    /// <summary>Replaces every block at once, for loading. Must be given Size*Height*Size entries.</summary>
    public void LoadBlocks(ReadOnlySpan<BlockType> blocks)
    {
        blocks.CopyTo(_blocks);

        TopY = 0;
        for (int i = blocks.Length - 1; i >= 0; i--)
            if (blocks[i] != BlockType.Air) { TopY = i / (Size * Size) + 1; break; }
        Version++;
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

    /// <summary>The river flow field as stored (one byte per column), for sending to clients.</summary>
    public ReadOnlySpan<byte> FlowCodes => _flow;

    public void LoadFlowCodes(ReadOnlySpan<byte> codes) => codes.CopyTo(_flow);

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

    /// <summary>
    /// Replaces the GPU buffers with a mesh built from the chunk as it was at <paramref name="version"/>. If the chunk has
    /// changed since, it stays flagged as needing a new mesh. Must run on the main thread (it creates GPU buffers).
    /// </summary>
    public void ApplyMesh(GraphicsDevice device, ChunkMeshData solid, ChunkMeshData water, int version)
    {
        if (device != null)
        {
            Mesh.Set(device, solid);
            WaterMesh.Set(device, water);
        }
        MeshedVersion = version;
    }

    public void Dispose()
    {
        Mesh.Dispose();
        WaterMesh.Dispose();
    }
}

/// <summary>Vertices and triangle indices for one batch of geometry, built on the CPU (any thread).</summary>
public sealed class ChunkMeshData
{
    public static readonly ChunkMeshData Empty = new(Array.Empty<VoxelVertex>(), Array.Empty<int>());

    public VoxelVertex[] Vertices { get; }
    public int[] Indices { get; }

    public ChunkMeshData(VoxelVertex[] vertices, int[] indices)
    {
        Vertices = vertices;
        Indices = indices;
    }
}

/// <summary>The GPU buffers for one batch of triangles.</summary>
public sealed class ChunkMesh : IDisposable
{
    public VertexBuffer VertexBuffer { get; private set; }
    public IndexBuffer IndexBuffer { get; private set; }
    public int IndexCount { get; private set; }
    public int VertexCount { get; private set; }

    public void Set(GraphicsDevice device, ChunkMeshData data)
    {
        Dispose();
        VertexCount = data.Vertices.Length;
        IndexCount = data.Indices.Length;
        if (IndexCount == 0) return;

        VertexBuffer = new VertexBuffer(device, VoxelVertex.VertexDeclaration, VertexCount, BufferUsage.WriteOnly);
        VertexBuffer.SetData(data.Vertices);

        // Most chunks have under 65,536 vertices, so 16-bit indices (half the size) are enough.
        if (VertexCount <= ushort.MaxValue)
        {
            var small = new ushort[IndexCount];
            for (int i = 0; i < small.Length; i++) small[i] = (ushort)data.Indices[i];
            IndexBuffer = new IndexBuffer(device, IndexElementSize.SixteenBits, IndexCount, BufferUsage.WriteOnly);
            IndexBuffer.SetData(small);
        }
        else
        {
            IndexBuffer = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, IndexCount, BufferUsage.WriteOnly);
            IndexBuffer.SetData(data.Indices);
        }
    }

    public void Dispose()
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
        VertexBuffer = null;
        IndexBuffer = null;
        IndexCount = 0;
        VertexCount = 0;
    }
}
