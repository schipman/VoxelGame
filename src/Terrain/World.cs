using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;
using VoxelGame.Persistence;
using VoxelGame.Rendering;

namespace VoxelGame.Terrain;

/// <summary>
/// The set of loaded chunks. Everything outside this class talks in world block coordinates;
/// the chunk lookup is handled here.
/// </summary>
public sealed class World
{
    private readonly Dictionary<Point, Chunk> _chunks = new();
    private readonly GraphicsDevice _device;
    private readonly WorldStorage _storage;   // null = nothing is saved or loaded
    private readonly List<Point> _offsets;   // chunk offsets around the player, nearest first
    private readonly List<Point> _unloadScratch = new();

    public int RenderDistance { get; private set; }
    public IReadOnlyCollection<Chunk> Chunks => _chunks.Values;
    public WorldGenerator Generator { get; }
    public LightingEngine Lighting { get; }
    public FluidSimulator Fluids { get; }
    public int Seed { get; }

    /// <summary>True once any block has been changed in this session; used to avoid saving untouched worlds.</summary>
    public bool HasBeenModified { get; private set; }

    public World(GraphicsDevice device, int seed, int renderDistance, WorldStorage storage = null)
    {
        _device = device;
        _storage = storage;
        Seed = seed;
        Generator = new WorldGenerator(seed);
        Lighting = new LightingEngine(this);
        Fluids = new FluidSimulator(this);
        _offsets = new List<Point>();
        SetRenderDistance(renderDistance);
    }

    /// <summary>Changes how far (in chunks) the world is loaded and drawn. Takes effect over the next few frames.</summary>
    public void SetRenderDistance(int chunks)
    {
        chunks = Math.Max(2, chunks);
        if (chunks == RenderDistance && _offsets.Count > 0) return;
        RenderDistance = chunks;

        // Two extra rings are generated (but not meshed): a mesh samples blocks and light from all 8
        // surrounding chunks (smooth lighting and ambient occlusion look one block past every edge).
        int r = chunks + 2;
        _offsets.Clear();
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
                if (dx * dx + dz * dz <= r * r)
                    _offsets.Add(new Point(dx, dz));
        _offsets.Sort((a, b) => (a.X * a.X + a.Y * a.Y).CompareTo(b.X * b.X + b.Y * b.Y));
    }

    public bool TryGetChunk(int chunkX, int chunkZ, out Chunk chunk) => _chunks.TryGetValue(new Point(chunkX, chunkZ), out chunk);

    // ---- Block access (world coordinates) ----

    public BlockType GetBlock(int x, int y, int z)
    {
        if (y < 0 || y >= Chunk.Height) return BlockType.Air;
        if (!_chunks.TryGetValue(new Point(x >> 4, z >> 4), out var chunk)) return BlockType.Air;
        return chunk.Get(x & 15, y, z & 15);
    }

    /// <summary>Fluid level at a position (0 = source block, 1-7 = flowing). Only meaningful where there is water.</summary>
    public int GetFluidLevel(int x, int y, int z)
    {
        if (y < 0 || y >= Chunk.Height) return 0;
        return _chunks.TryGetValue(new Point(x >> 4, z >> 4), out var chunk) ? chunk.GetFluidLevel(x & 15, y, z & 15) : 0;
    }

    public bool IsWater(int x, int y, int z) => BlockRegistry.IsLiquid(GetBlock(x, y, z));

    /// <summary>
    /// World height of the water surface above a position, or null if that cell isn't water.
    /// Follows the column of water upward to its top, then takes that block's fluid height.
    /// </summary>
    public float? WaterSurface(int x, int y, int z)
    {
        if (!IsWater(x, y, z)) return null;
        int top = y;
        while (top + 1 < Chunk.Height && IsWater(x, top + 1, z)) top++;
        return top + FluidSimulator.SurfaceHeight(GetFluidLevel(x, top, z));
    }

    /// <summary>Block plus both light levels in one lookup; used by the mesher.</summary>
    public void GetCell(int x, int y, int z, out BlockType type, out int skyLight, out int blockLight)
    {
        if (y >= Chunk.Height) { type = BlockType.Air; skyLight = LightingEngine.MaxLight; blockLight = 0; return; }
        if (y < 0 || !_chunks.TryGetValue(new Point(x >> 4, z >> 4), out var chunk))
        {
            type = BlockType.Air; skyLight = 0; blockLight = 0;
            return;
        }
        int lx = x & 15, lz = z & 15;
        type = chunk.Get(lx, y, lz);
        skyLight = chunk.GetSkyLight(lx, y, lz);
        blockLight = chunk.GetBlockLight(lx, y, lz);
    }

    /// <summary>
    /// Changes a block, updating light, meshes, the save flag and any water nearby. For liquids, fluidLevel is
    /// 0 for a source block or 1-7 for flowing water. Placing a block into water simply replaces it.
    /// </summary>
    public void SetBlock(int x, int y, int z, BlockType type, int fluidLevel = 0)
    {
        if (y < 0 || y >= Chunk.Height) return;
        if (!_chunks.TryGetValue(new Point(x >> 4, z >> 4), out var chunk)) return;

        int lx = x & 15, lz = z & 15;
        bool liquid = BlockRegistry.IsLiquid(type);
        if (!liquid) fluidLevel = 0;
        if (chunk.Get(lx, y, lz) == type && (!liquid || chunk.GetFluidLevel(lx, y, lz) == fluidLevel)) return;

        chunk.Set(lx, y, lz, type);
        chunk.SetFluidLevel(lx, y, lz, fluidLevel);
        chunk.NeedsSave = true;
        HasBeenModified = true;
        MarkMeshDirty(chunk, lx, lz);
        Lighting.OnBlockChanged(x, y, z, type);
        Fluids.OnBlockChanged(x, y, z);
    }

    /// <summary>Solid for collision purposes. Unloaded chunks count as solid so nothing falls into the void.</summary>
    public bool IsSolid(int x, int y, int z)
    {
        if (y < 0) return true;
        if (y >= Chunk.Height) return false;
        if (!_chunks.TryGetValue(new Point(x >> 4, z >> 4), out var chunk)) return true;
        return BlockRegistry.Get(chunk.Get(x & 15, y, z & 15)).IsSolid;
    }

    /// <summary>
    /// Flags a chunk for re-meshing after a block or light change at (lx, lz). Meshes read one cell
    /// past their edges, so changes on an edge or corner also dirty the chunks next to it.
    /// </summary>
    public void MarkMeshDirty(Chunk chunk, int lx, int lz)
    {
        chunk.NeedsMesh = true;

        int dx = lx == 0 ? -1 : lx == Chunk.Size - 1 ? 1 : 0;
        int dz = lz == 0 ? -1 : lz == Chunk.Size - 1 ? 1 : 0;
        if (dx == 0 && dz == 0) return;

        if (dx != 0) MarkDirty(chunk.ChunkX + dx, chunk.ChunkZ);
        if (dz != 0) MarkDirty(chunk.ChunkX, chunk.ChunkZ + dz);
        if (dx != 0 && dz != 0) MarkDirty(chunk.ChunkX + dx, chunk.ChunkZ + dz);
    }

    private void MarkDirty(int chunkX, int chunkZ)
    {
        if (_chunks.TryGetValue(new Point(chunkX, chunkZ), out var c)) c.NeedsMesh = true;
    }

    /// <summary>Advances time-based simulation (water flow). Call once per frame.</summary>
    public void Tick(float dt) => Fluids.Update(dt);

    // ---- Streaming ----

    /// <summary>
    /// Generates, meshes and unloads chunks around a position. The budgets cap the work done per call so the frame
    /// rate stays smooth: at most maxGenerate new chunks and maxMesh meshes, and no more than timeBudgetMs of work
    /// (checked after each chunk, so one chunk can overshoot a little). Pass int.MaxValue and no time budget to
    /// load everything at once, e.g. at startup.
    /// </summary>
    /// <remarks>
    /// The time budget matters because MonoGame stops drawing when Update can't keep up with 60 calls a second, so a
    /// fixed number of chunks per frame can freeze the picture whenever chunks are expensive.
    /// </remarks>
    public void Update(Vector3 center, int maxGenerate, int maxMesh, double timeBudgetMs = double.MaxValue)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        int cx = (int)MathF.Floor(center.X) >> 4;
        int cz = (int)MathF.Floor(center.Z) >> 4;

        int generated = 0;
        foreach (var o in _offsets)
        {
            if (generated >= maxGenerate || (generated > 0 && clock.Elapsed.TotalMilliseconds > timeBudgetMs)) break;
            var key = new Point(cx + o.X, cz + o.Y);
            if (_chunks.ContainsKey(key)) continue;

            var chunk = new Chunk(key.X, key.Y);
            // A chunk the player edited comes from disk; anything else is generated from the seed.
            if (_storage != null && _storage.TryLoadChunk(chunk))
                Generator.FillRiverFlow(chunk);   // rivers are not saved, only the blocks
            else
                Generator.Generate(chunk);
            _chunks[key] = chunk;
            Lighting.InitializeChunk(chunk);
            generated++;
        }

        int meshed = 0;
        int rd2 = RenderDistance * RenderDistance;
        foreach (var o in _offsets)
        {
            if (meshed >= maxMesh || (meshed > 0 && clock.Elapsed.TotalMilliseconds > timeBudgetMs)) break;
            if (o.X * o.X + o.Y * o.Y > rd2) continue;
            if (!_chunks.TryGetValue(new Point(cx + o.X, cz + o.Y), out var chunk)) continue;
            if (!chunk.NeedsMesh || !HasAllNeighbours(chunk)) continue;

            ChunkMesher.Build(chunk, this, _device);
            meshed++;
        }

        // Unload chunks that are well outside the loaded area.
        int unloadDist = RenderDistance + 4;
        _unloadScratch.Clear();
        foreach (var (key, _) in _chunks)
            if (Math.Abs(key.X - cx) > unloadDist || Math.Abs(key.Y - cz) > unloadDist)
                _unloadScratch.Add(key);
        foreach (var key in _unloadScratch)
        {
            SaveChunk(_chunks[key]);
            _chunks[key].Dispose();
            _chunks.Remove(key);
        }
    }

    // ---- Saving ----

    /// <summary>Writes every changed chunk to disk. Returns how many were saved.</summary>
    public int SaveModifiedChunks()
    {
        int saved = 0;
        foreach (var chunk in _chunks.Values)
            if (chunk.NeedsSave) { SaveChunk(chunk); saved++; }
        return saved;
    }

    private void SaveChunk(Chunk chunk)
    {
        if (_storage == null || !chunk.NeedsSave) return;
        _storage.SaveChunk(chunk);
        chunk.NeedsSave = false;
    }

    private bool HasAllNeighbours(Chunk c)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if ((dx != 0 || dz != 0) && !_chunks.ContainsKey(new Point(c.ChunkX + dx, c.ChunkZ + dz)))
                    return false;
        return true;
    }
}
