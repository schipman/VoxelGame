using VoxelGame.Blocks;

namespace VoxelGame.Terrain;

/// <summary>
/// A chunk together with the eight around it, captured as plain references. The mesher reads blocks and light up to one
/// cell past the chunk's edges (smooth lighting and ambient occlusion look at the neighbours), and this gives it array
/// lookups for that instead of dictionary lookups, and lets it run on a worker thread: the set of chunks it reads is
/// fixed when it is captured, whatever the main thread does to the world afterwards.
///
/// Coordinates are local to the centre chunk and may range from -16 to 31 on x and z.
/// </summary>
public sealed class ChunkNeighborhood
{
    private readonly Chunk[] _chunks = new Chunk[9];   // index (dz + 1) * 3 + (dx + 1)

    public Chunk Center => _chunks[4];

    /// <summary>Captures the 3x3 block of chunks around a chunk, or returns null if any of them isn't loaded.</summary>
    public static ChunkNeighborhood Capture(World world, Chunk center)
    {
        var n = new ChunkNeighborhood();
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var chunk = dx == 0 && dz == 0 ? center : null;
                if (chunk == null && !world.TryGetChunk(center.ChunkX + dx, center.ChunkZ + dz, out chunk)) return null;
                n._chunks[(dz + 1) * 3 + dx + 1] = chunk;
            }
        return n;
    }

    private Chunk ChunkAt(int x, int z) => _chunks[((z >> 4) + 1) * 3 + (x >> 4) + 1];

    public BlockType GetBlock(int x, int y, int z)
    {
        if ((uint)y >= Chunk.Height) return BlockType.Air;
        return ChunkAt(x, z).Get(x & 15, y, z & 15);
    }

    /// <summary>Block and both light levels at once. Above the world is open sky; below it is dark.</summary>
    public void GetCell(int x, int y, int z, out BlockType type, out int skyLight, out int blockLight)
    {
        if (y >= Chunk.Height) { type = BlockType.Air; skyLight = LightingEngine.MaxLight; blockLight = 0; return; }
        if (y < 0) { type = BlockType.Air; skyLight = 0; blockLight = 0; return; }

        var chunk = ChunkAt(x, z);
        int lx = x & 15, lz = z & 15;
        type = chunk.Get(lx, y, lz);
        skyLight = chunk.GetSkyLight(lx, y, lz);
        blockLight = chunk.GetBlockLight(lx, y, lz);
    }

    public int GetFluidLevel(int x, int y, int z)
    {
        if ((uint)y >= Chunk.Height) return 0;
        return ChunkAt(x, z).GetFluidLevel(x & 15, y, z & 15);
    }
}
