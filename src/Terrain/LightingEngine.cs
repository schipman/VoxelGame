using VoxelGame.Blocks;

namespace VoxelGame.Terrain;

public enum LightChannel { Sky, Block }

/// <summary>
/// Minecraft-style voxel lighting. Every voxel stores two light levels (0-15):
///   Sky   - daylight. 15 in any column open to the sky, spreading sideways into overhangs and caves.
///   Block - light from emissive blocks such as lamps.
/// Light spreads outward one level dimmer per block through non-opaque blocks (breadth-first flood fill).
/// Sky light travelling straight down does not dim, so sunlit shafts stay bright all the way to the bottom,
/// unless it passes through something that dims skylight (leaves), after which it fades like any other light.
/// Placing or breaking a block "un-spreads" the light that depended on it and re-floods from what is left.
/// </summary>
public sealed class LightingEngine
{
    public const int MaxLight = 15;

    private readonly record struct LightNode(int X, int Y, int Z, int Level);

    private static readonly (int X, int Y, int Z)[] Directions =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
    };
    private const int Down = 3;   // index of (0,-1,0) above

    private readonly World _world;
    private readonly Queue<LightNode> _spread = new();
    private readonly Queue<LightNode> _remove = new();

    // Consecutive lookups almost always hit the same chunk, so remember the last one.
    private Chunk _cachedChunk;
    private int _cachedChunkX, _cachedChunkZ;
    private bool _cacheValid;

    public LightingEngine(World world)
    {
        _world = world;
    }

    // ---- Public entry points ----

    /// <summary>
    /// Lights a freshly generated chunk and exchanges light with any loaded neighbours.
    /// The order chunks are generated in does not matter: each new chunk both pushes its light
    /// out into existing neighbours and pulls theirs in.
    /// </summary>
    public void InitializeChunk(Chunk chunk)
    {
        _cacheValid = false;

        // Sunlight falls straight down every column until it hits something opaque. Full-strength light
        // passes through air unchanged; once something has dimmed it (leaves), it fades one level per block.
        for (int x = 0; x < Chunk.Size; x++)
        for (int z = 0; z < Chunk.Size; z++)
        {
            int level = MaxLight;
            for (int y = Chunk.Height - 1; y >= 0 && level > 0; y--)
            {
                var type = chunk.Get(x, y, z);
                if (BlockRegistry.IsOpaque(type)) break;
                level = SkyLevelBelow(MaxLight == level, level, type);
                chunk.SetSkyLight(x, y, z, level);
            }
        }

        // Emissive blocks light themselves.
        for (int y = 0; y < Chunk.Height; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            int emission = BlockRegistry.Get(chunk.Get(x, y, z)).LightEmission;
            if (emission > 0) chunk.SetBlockLight(x, y, z, emission);
        }

        foreach (var channel in new[] { LightChannel.Sky, LightChannel.Block })
        {
            // Seeds: cells of this chunk that can brighten a neighbour...
            for (int y = 0; y < Chunk.Height; y++)
            for (int z = 0; z < Chunk.Size; z++)
            for (int x = 0; x < Chunk.Size; x++)
            {
                int wx = chunk.WorldX + x, wz = chunk.WorldZ + z;
                int level = Get(channel, wx, y, wz);
                if (level > 1 && CanSpread(channel, wx, y, wz, level))
                    _spread.Enqueue(new LightNode(wx, y, wz, level));
            }

            // ...and the border cells of loaded neighbours, which may now spill into this chunk.
            for (int i = 0; i < Chunk.Size; i++)
            for (int y = 0; y < Chunk.Height; y++)
            {
                SeedBorder(channel, chunk.WorldX - 1,         y, chunk.WorldZ + i);
                SeedBorder(channel, chunk.WorldX + Chunk.Size, y, chunk.WorldZ + i);
                SeedBorder(channel, chunk.WorldX + i, y, chunk.WorldZ - 1);
                SeedBorder(channel, chunk.WorldX + i, y, chunk.WorldZ + Chunk.Size);
            }

            Spread(channel);
        }
    }

    /// <summary>
    /// Call after a block changed. The new block must already be stored in the world.
    /// Removes light that depended on the old state of the cell, then floods in whatever the new state allows.
    /// </summary>
    public void OnBlockChanged(int x, int y, int z, BlockType newType)
    {
        _cacheValid = false;

        foreach (var channel in new[] { LightChannel.Sky, LightChannel.Block })
        {
            int level = Get(channel, x, y, z);
            if (level > 0) Remove(channel, x, y, z, level);
        }

        int emission = BlockRegistry.Get(newType).LightEmission;
        if (emission > 0)
        {
            Set(LightChannel.Block, x, y, z, emission);
            _spread.Enqueue(new LightNode(x, y, z, emission));
            Spread(LightChannel.Block);
        }

        // An opened-up cell is lit by whichever neighbours have light to give. At the very top of the world
        // there is no neighbour above, only open sky.
        if (!BlockRegistry.IsOpaque(newType))
        {
            if (y == Chunk.Height - 1)
            {
                int top = SkyLevelBelow(true, MaxLight, newType);
                Set(LightChannel.Sky, x, y, z, top);
                _spread.Enqueue(new LightNode(x, y, z, top));
                Spread(LightChannel.Sky);
            }

            foreach (var channel in new[] { LightChannel.Sky, LightChannel.Block })
            {
                foreach (var d in Directions)
                    SeedBorder(channel, x + d.X, y + d.Y, z + d.Z);
                Spread(channel);
            }
        }
    }

    // ---- Flood fill ----

    /// <summary>Breadth-first spread of everything in the queue.</summary>
    private void Spread(LightChannel channel)
    {
        while (_spread.TryDequeue(out var node))
        {
            int level = Get(channel, node.X, node.Y, node.Z);
            if (level <= 1) continue;

            for (int d = 0; d < Directions.Length; d++)
            {
                int nx = node.X + Directions[d].X, ny = node.Y + Directions[d].Y, nz = node.Z + Directions[d].Z;
                if ((uint)ny >= Chunk.Height) continue;
                var chunk = ChunkAt(nx, nz);
                if (chunk == null) continue;

                int lx = nx & 15, lz = nz & 15;
                var target = chunk.Get(lx, ny, lz);
                if (BlockRegistry.IsOpaque(target)) continue;

                int newLevel = SpreadLevel(channel, d, level, target);
                if (newLevel <= GetLocal(channel, chunk, lx, ny, lz)) continue;

                SetLocal(channel, chunk, lx, ny, lz, newLevel);
                _spread.Enqueue(new LightNode(nx, ny, nz, newLevel));
            }
        }
    }

    /// <summary>
    /// Zeroes the light at a cell and everything that was lit through it. Cells that are lit by
    /// something else are queued, and Spread then refills the hole from them.
    /// </summary>
    private void Remove(LightChannel channel, int x, int y, int z, int level)
    {
        Set(channel, x, y, z, 0);
        _remove.Enqueue(new LightNode(x, y, z, level));

        while (_remove.TryDequeue(out var node))
        {
            for (int d = 0; d < Directions.Length; d++)
            {
                int nx = node.X + Directions[d].X, ny = node.Y + Directions[d].Y, nz = node.Z + Directions[d].Z;
                if ((uint)ny >= Chunk.Height) continue;
                var chunk = ChunkAt(nx, nz);
                if (chunk == null) continue;

                int lx = nx & 15, lz = nz & 15;
                int neighbour = GetLocal(channel, chunk, lx, ny, lz);
                if (neighbour == 0) continue;

                // Dimmer than us (or part of a full-strength sky shaft going down) means it got its light from us.
                bool dependent = neighbour < node.Level
                    || (channel == LightChannel.Sky && d == Down && node.Level == MaxLight && neighbour == MaxLight);

                // A lamp's own glow never came from a neighbour, so it must survive (and re-spread).
                if (dependent && channel == LightChannel.Block &&
                    BlockRegistry.Get(chunk.Get(lx, ny, lz)).LightEmission >= neighbour)
                    dependent = false;

                if (dependent)
                {
                    SetLocal(channel, chunk, lx, ny, lz, 0);
                    _remove.Enqueue(new LightNode(nx, ny, nz, neighbour));
                }
                else
                {
                    _spread.Enqueue(new LightNode(nx, ny, nz, neighbour));
                }
            }
        }

        Spread(channel);
    }

    /// <summary>Light level a cell receives from a neighbour of the given level in the given direction.</summary>
    private static int SpreadLevel(LightChannel channel, int direction, int level, BlockType target) =>
        channel == LightChannel.Sky && direction == Down
            ? SkyLevelBelow(level == MaxLight, level, target)
            : level - 1;

    /// <summary>
    /// Sky light of a cell directly below one with the given level: full-strength light stays full through
    /// plain air, and drops one level through leaves; anything weaker fades one level per block.
    /// </summary>
    private static int SkyLevelBelow(bool fullStrength, int levelAbove, BlockType cell) =>
        fullStrength && !BlockRegistry.DimsSkylight(cell) ? MaxLight : Math.Max(levelAbove - 1, 0);

    /// <summary>True if this cell would brighten at least one of its neighbours.</summary>
    private bool CanSpread(LightChannel channel, int x, int y, int z, int level)
    {
        for (int d = 0; d < Directions.Length; d++)
        {
            int nx = x + Directions[d].X, ny = y + Directions[d].Y, nz = z + Directions[d].Z;
            if ((uint)ny >= Chunk.Height) continue;
            var chunk = ChunkAt(nx, nz);
            if (chunk == null) continue;

            int lx = nx & 15, lz = nz & 15;
            var target = chunk.Get(lx, ny, lz);
            if (BlockRegistry.IsOpaque(target)) continue;
            if (SpreadLevel(channel, d, level, target) > GetLocal(channel, chunk, lx, ny, lz)) return true;
        }
        return false;
    }

    /// <summary>Queues a cell as a spreading source if it has light to give.</summary>
    private void SeedBorder(LightChannel channel, int x, int y, int z)
    {
        if ((uint)y >= Chunk.Height) return;
        int level = Get(channel, x, y, z);
        if (level > 1) _spread.Enqueue(new LightNode(x, y, z, level));
    }

    // ---- Light access by world coordinates ----

    private Chunk ChunkAt(int x, int z)
    {
        int cx = x >> 4, cz = z >> 4;
        if (!_cacheValid || cx != _cachedChunkX || cz != _cachedChunkZ)
        {
            _world.TryGetChunk(cx, cz, out _cachedChunk);
            _cachedChunkX = cx;
            _cachedChunkZ = cz;
            _cacheValid = true;
        }
        return _cachedChunk;
    }

    private int Get(LightChannel channel, int x, int y, int z)
    {
        var chunk = ChunkAt(x, z);
        return chunk == null ? 0 : GetLocal(channel, chunk, x & 15, y, z & 15);
    }

    private void Set(LightChannel channel, int x, int y, int z, int level)
    {
        var chunk = ChunkAt(x, z);
        if (chunk != null) SetLocal(channel, chunk, x & 15, y, z & 15, level);
    }

    private static int GetLocal(LightChannel channel, Chunk chunk, int lx, int y, int lz) =>
        channel == LightChannel.Sky ? chunk.GetSkyLight(lx, y, lz) : chunk.GetBlockLight(lx, y, lz);

    private void SetLocal(LightChannel channel, Chunk chunk, int lx, int y, int lz, int level)
    {
        if (channel == LightChannel.Sky) chunk.SetSkyLight(lx, y, lz, level);
        else chunk.SetBlockLight(lx, y, lz, level);

        // The mesh bakes in light, so the chunk (and any neighbour that samples across the edge) must be rebuilt.
        _world.MarkMeshDirty(chunk, lx, lz);
    }
}
