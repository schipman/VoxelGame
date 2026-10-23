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
public sealed class World : IDisposable
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

    /// <summary>For tests and tools: called whenever a finished mesh is applied to a chunk, whichever thread built it.</summary>
    public event Action<Chunk, ChunkMeshData, ChunkMeshData, int> MeshApplied;

    private readonly ChunkWorkers _workers;                       // null = do everything on the calling thread
    private readonly HashSet<Point> _generating = new();           // chunks a worker is generating right now
    private int _meshesInFlight;
    private readonly HashSet<Chunk> _urgent = new();               // chunks touched by an edit: meshed immediately, not queued
    private bool _editing;

    /// <param name="workerThreads">
    /// How many background threads generate and mesh chunks. With 0 everything happens on the calling thread, one step per
    /// <see cref="Update"/> call, which is what tests want (the result doesn't depend on timing).
    /// </param>
    public World(GraphicsDevice device, int seed, int renderDistance, WorldStorage storage = null, int workerThreads = 0)
    {
        _device = device;
        _storage = storage;
        Seed = seed;
        Generator = new WorldGenerator(seed);
        Lighting = new LightingEngine(this);
        Fluids = new FluidSimulator(this);
        if (workerThreads > 0) _workers = new ChunkWorkers(workerThreads, Generator, storage);
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

        // Chunks touched by an edit are meshed straight away (see Update), so the player sees the change on the next frame.
        _editing = true;
        try
        {
            chunk.Set(lx, y, lz, type);
            chunk.SetFluidLevel(lx, y, lz, fluidLevel);
            chunk.NeedsSave = true;
            HasBeenModified = true;
            MarkMeshDirty(chunk, lx, lz);
            Lighting.OnBlockChanged(x, y, z, type);
        }
        finally { _editing = false; }
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
        chunk.MarkDirty();
        if (_editing) _urgent.Add(chunk);

        int dx = lx == 0 ? -1 : lx == Chunk.Size - 1 ? 1 : 0;
        int dz = lz == 0 ? -1 : lz == Chunk.Size - 1 ? 1 : 0;
        if (dx == 0 && dz == 0) return;

        if (dx != 0) MarkDirty(chunk.ChunkX + dx, chunk.ChunkZ);
        if (dz != 0) MarkDirty(chunk.ChunkX, chunk.ChunkZ + dz);
        if (dx != 0 && dz != 0) MarkDirty(chunk.ChunkX + dx, chunk.ChunkZ + dz);
    }

    private void MarkDirty(int chunkX, int chunkZ)
    {
        if (!_chunks.TryGetValue(new Point(chunkX, chunkZ), out var c)) return;
        c.MarkDirty();
        if (_editing) _urgent.Add(c);
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

        if (_workers == null) UpdateInline(cx, cz, maxGenerate, maxMesh, timeBudgetMs, clock);
        else UpdateWithWorkers(cx, cz, maxGenerate, maxMesh, timeBudgetMs, clock);

        Unload(cx, cz);
    }

    /// <summary>One thread does everything: simple and deterministic, used by tests.</summary>
    private void UpdateInline(int cx, int cz, int maxGenerate, int maxMesh, double timeBudgetMs, System.Diagnostics.Stopwatch clock)
    {
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
            AddChunk(chunk);
            generated++;
        }

        MeshUrgent(clock, timeBudgetMs);

        int meshed = 0;
        int rd2 = RenderDistance * RenderDistance;
        foreach (var o in _offsets)
        {
            if (meshed >= maxMesh || (meshed > 0 && clock.Elapsed.TotalMilliseconds > timeBudgetMs)) break;
            if (o.X * o.X + o.Y * o.Y > rd2) continue;
            if (!_chunks.TryGetValue(new Point(cx + o.X, cz + o.Y), out var chunk)) continue;
            if (!chunk.NeedsMesh || !HasAllNeighbours(chunk)) continue;

            BuildMesh(chunk);
            meshed++;
        }
    }

    /// <summary>
    /// Worker threads generate and mesh; this thread only hands out the jobs, lights new chunks (which edits the
    /// neighbouring chunks, so it can't run beside other work) and uploads finished meshes. maxGenerate and maxMesh limit how
    /// many jobs are handed out per call; timeBudgetMs limits the time spent lighting and uploading.
    /// </summary>
    private void UpdateWithWorkers(int cx, int cz, int maxGenerate, int maxMesh, double timeBudgetMs, System.Diagnostics.Stopwatch clock)
    {
        int inFlightLimit = _workers.Count * 4;   // enough to keep every thread busy, few enough that stale jobs don't pile up

        // 1. Finished chunks: insert them and light them.
        int unloadDist = RenderDistance + 4;
        while (_workers.TryTakeGenerated(out var done))
        {
            _generating.Remove(new Point(done.ChunkX, done.ChunkZ));
            if (done.Chunk == null) continue;   // the worker failed; the chunk is simply requested again below
            if (Math.Abs(done.ChunkX - cx) > unloadDist || Math.Abs(done.ChunkZ - cz) > unloadDist) continue;   // the player has moved away
            if (_chunks.ContainsKey(new Point(done.ChunkX, done.ChunkZ))) continue;
            AddChunk(done.Chunk);
            if (clock.Elapsed.TotalMilliseconds > timeBudgetMs) break;
        }

        // 2. Ask for chunks that are missing, nearest first.
        int requested = 0;
        foreach (var o in _offsets)
        {
            if (requested >= maxGenerate || _generating.Count >= inFlightLimit) break;
            var key = new Point(cx + o.X, cz + o.Y);
            if (_chunks.ContainsKey(key) || !_generating.Add(key)) continue;
            _workers.RequestGenerate(key.X, key.Y);
            requested++;
        }

        // 3. Edits are meshed right here, so a broken block disappears on the very next frame.
        MeshUrgent(clock, timeBudgetMs);

        // 4. Upload finished meshes.
        while (_workers.TryTakeMeshed(out var meshed))
        {
            _meshesInFlight--;
            meshed.Chunk.MeshInFlight = false;
            // The chunk may have been unloaded meanwhile. If it was edited, ApplyMesh leaves it marked as needing a new mesh.
            if (_chunks.TryGetValue(new Point(meshed.Chunk.ChunkX, meshed.Chunk.ChunkZ), out var current) && current == meshed.Chunk)
                ApplyMesh(meshed.Chunk, meshed.Solid, meshed.Water, meshed.Version);
            if (clock.Elapsed.TotalMilliseconds > timeBudgetMs) break;
        }

        // 5. Hand out meshing jobs for chunks that have all their neighbours, nearest first.
        int queued = 0;
        int rd2 = RenderDistance * RenderDistance;
        foreach (var o in _offsets)
        {
            if (queued >= maxMesh || _meshesInFlight >= inFlightLimit) break;
            if (o.X * o.X + o.Y * o.Y > rd2) continue;
            if (!_chunks.TryGetValue(new Point(cx + o.X, cz + o.Y), out var chunk)) continue;
            if (!chunk.NeedsMesh || chunk.MeshInFlight) continue;

            var neighbourhood = ChunkNeighborhood.Capture(this, chunk);
            if (neighbourhood == null) continue;

            chunk.MeshInFlight = true;
            _meshesInFlight++;
            queued++;
            _workers.RequestMesh(new MeshRequest { Chunk = chunk, Neighborhood = neighbourhood, Version = chunk.Version });
        }
    }

    /// <summary>Inserts a freshly generated chunk and lights it.</summary>
    private void AddChunk(Chunk chunk)
    {
        _chunks[new Point(chunk.ChunkX, chunk.ChunkZ)] = chunk;
        Lighting.InitializeChunk(chunk);
    }

    /// <summary>Meshes the chunks an edit touched, on this thread, within the time budget (the rest wait for the next frame).</summary>
    private void MeshUrgent(System.Diagnostics.Stopwatch clock, double timeBudgetMs)
    {
        if (_urgent.Count == 0) return;
        List<Chunk> finished = null;
        foreach (var chunk in _urgent)
        {
            bool loaded = _chunks.TryGetValue(new Point(chunk.ChunkX, chunk.ChunkZ), out var current) && current == chunk;
            if (loaded && chunk.NeedsMesh && !chunk.MeshInFlight)
            {
                if (clock.Elapsed.TotalMilliseconds > Math.Max(timeBudgetMs, 4)) break;
                if (!BuildMesh(chunk)) continue;   // neighbours missing: try again next frame
            }
            // Done, unloaded, or already being meshed by a worker (its result will be stale, so it gets rebuilt).
            (finished ??= new List<Chunk>()).Add(chunk);
        }
        if (finished != null) foreach (var chunk in finished) _urgent.Remove(chunk);
    }

    /// <summary>Unloads chunks that are well outside the loaded area, saving any with changes.</summary>
    private void Unload(int cx, int cz)
    {
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
            MeshRevision++;
        }
    }

    /// <summary>
    /// Loads everything around a position and waits for it to finish. Used at start-up, so the first frame shows a complete
    /// world. With worker threads this is where they pay off: the waiting is spent generating and meshing in parallel.
    /// </summary>
    public void LoadAll(Vector3 center)
    {
        while (true)
        {
            Update(center, int.MaxValue, int.MaxValue);
            if (_generating.Count == 0 && _meshesInFlight == 0 && CountPendingWork(center) == 0) break;
            Thread.Sleep(1);
        }
    }

    /// <summary>Chunks being generated or meshed by worker threads right now.</summary>
    public int WorkInFlight => _generating.Count + _meshesInFlight;

    public void Dispose() => _workers?.Dispose();

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

    /// <summary>How much loading work is left around the last position: chunks not yet generated plus meshes not yet built.</summary>
    public int CountPendingWork(Vector3 center)
    {
        int cx = (int)MathF.Floor(center.X) >> 4, cz = (int)MathF.Floor(center.Z) >> 4;
        int pending = 0, rd2 = RenderDistance * RenderDistance;
        foreach (var o in _offsets)
        {
            if (!_chunks.TryGetValue(new Point(cx + o.X, cz + o.Y), out var chunk)) { pending++; continue; }
            if (o.X * o.X + o.Y * o.Y <= rd2 && (chunk.NeedsMesh || chunk.MeshInFlight) && HasAllNeighbours(chunk)) pending++;
        }
        return pending;
    }

    private readonly ChunkMesher _mesher = new();

    /// <summary>
    /// Builds a chunk's mesh right now, on the calling thread, and uploads it. Needs all eight neighbours loaded.
    /// (Without a graphics device, as in tests, the mesh is built and discarded; the chunk is still marked as meshed.)
    /// </summary>
    public bool BuildMesh(Chunk chunk)
    {
        var neighbourhood = ChunkNeighborhood.Capture(this, chunk);
        if (neighbourhood == null) return false;

        int version = chunk.Version;
        var solid = _mesher.Build(neighbourhood, out var water);
        ApplyMesh(chunk, solid, water, version);
        return true;
    }

    /// <summary>Increases whenever any chunk's mesh changes or a chunk is unloaded; lets the renderer know when its shadow map is out of date.</summary>
    public int MeshRevision { get; private set; }

    private void ApplyMesh(Chunk chunk, ChunkMeshData solid, ChunkMeshData water, int version)
    {
        MeshRevision++;
        chunk.ApplyMesh(_device, solid, water, version);
        MeshApplied?.Invoke(chunk, solid, water, version);
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
