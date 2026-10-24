using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;
using VoxelGame.Net;
using VoxelGame.Persistence;
using VoxelGame.Rendering;

namespace VoxelGame.Terrain;

/// <summary>Which part of the game a world serves. The same class is used by all of them; the role decides what it bothers doing.</summary>
public enum WorldRole
{
    /// <summary>Generates, simulates, lights and meshes everything itself. What tests use, and what a standalone game would be.</summary>
    Full,

    /// <summary>The authoritative copy: generates chunks, simulates water, saves. Never lights or meshes (nobody looks at it).</summary>
    Server,

    /// <summary>What a player sees: chunks come from the server, and are lit and meshed here. Never generates, simulates or saves.</summary>
    Client,
}

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
    private int _decoding;                                          // chunks a worker is unpacking (client)

    public WorldRole Role { get; }

    // What each role does (see WorldRole).
    private bool Generates => Role != WorldRole.Client;
    private bool Simulates => Role != WorldRole.Client;
    private bool Lights => Role != WorldRole.Server;
    private bool Meshes => Role != WorldRole.Server;
    private bool AutoUnloads => Role != WorldRole.Client;

    /// <summary>
    /// Raised after every change made through <see cref="SetBlock"/> (by the player or by flowing water), with the block and its
    /// fluid level. A server listens to tell its players.
    /// </summary>
    public event Action<int, int, int, BlockType, int> BlockChanged;

    /// <param name="workerThreads">
    /// How many background threads generate and mesh chunks. With 0 everything happens on the calling thread, one step per
    /// <see cref="Update"/> call, which is what tests want (the result doesn't depend on timing).
    /// </param>
    public World(GraphicsDevice device, int seed, int renderDistance, WorldStorage storage = null, int workerThreads = 0, WorldRole role = WorldRole.Full)
    {
        Role = role;
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

    /// <summary>True if every chunk within a radius (in chunks) of a position is loaded.</summary>
    public bool HasChunksAround(Vector3 center, int radius)
    {
        int cx = (int)MathF.Floor(center.X) >> 4, cz = (int)MathF.Floor(center.Z) >> 4;
        for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
                if (dx * dx + dz * dz <= radius * radius && !_chunks.ContainsKey(new Point(cx + dx, cz + dz))) return false;
        return true;
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
    public void SetBlock(int x, int y, int z, BlockType type, int fluidLevel = 0) => ChangeBlock(x, y, z, type, fluidLevel, fromServer: false);

    /// <summary>A client applying a change the server announced. Like SetBlock, but nothing is saved or simulated here: the server does that.</summary>
    public void ApplyRemoteBlock(int x, int y, int z, BlockType type, int fluidLevel) => ChangeBlock(x, y, z, type, fluidLevel, fromServer: true);

    private void ChangeBlock(int x, int y, int z, BlockType type, int fluidLevel, bool fromServer)
    {
        if (y < 0 || y >= Chunk.Height) return;
        if (!_chunks.TryGetValue(new Point(x >> 4, z >> 4), out var chunk)) return;

        int lx = x & 15, lz = z & 15;
        bool liquid = BlockRegistry.IsLiquid(type);
        if (!liquid) fluidLevel = 0;
        if (chunk.Get(lx, y, lz) == type && (!liquid || chunk.GetFluidLevel(lx, y, lz) == fluidLevel)) return;

        // Chunks touched by an edit are meshed straight away (see Update), so the player sees the change on the next frame.
        _editing = Meshes;
        try
        {
            chunk.Set(lx, y, lz, type);
            chunk.SetFluidLevel(lx, y, lz, fluidLevel);
            if (!fromServer)
            {
                chunk.NeedsSave = true;
                HasBeenModified = true;
            }
            MarkMeshDirty(chunk, lx, lz);
            if (Lights) Lighting.OnBlockChanged(x, y, z, type);
        }
        finally { _editing = false; }

        if (fromServer) return;
        if (Simulates) Fluids.OnBlockChanged(x, y, z);
        BlockChanged?.Invoke(x, y, z, type, fluidLevel);
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
    public void Tick(float dt)
    {
        if (Simulates) Fluids.Update(dt);
    }

    // ---- Streaming ----

    /// <summary>The chunk containing each position we are loading around (the player; on a server, every player).</summary>
    private readonly List<Point> _centers = new();

    /// <summary>Loads, meshes and unloads chunks around one position. See the overload below.</summary>
    public void Update(Vector3 center, int maxGenerate, int maxMesh, double timeBudgetMs = double.MaxValue) =>
        Update(new[] { center }, maxGenerate, maxMesh, timeBudgetMs);

    /// <summary>
    /// Keeps the world loaded around some positions. What that means depends on the <see cref="Role"/>:
    /// a full world or a server generates missing chunks; a client waits for the server to send them; everything but a
    /// server lights and meshes them. The budgets cap the work done per call so the frame rate stays smooth: at most
    /// maxGenerate chunks requested and maxMesh meshes started, and no more than timeBudgetMs of work on this thread
    /// (checked after each chunk, so one chunk can overshoot a little). Pass int.MaxValue and no time budget to load
    /// everything at once, e.g. at startup.
    /// </summary>
    /// <remarks>
    /// The time budget matters because MonoGame stops drawing when Update can't keep up with 60 calls a second, so a
    /// fixed number of chunks per frame can freeze the picture whenever chunks are expensive.
    /// </remarks>
    public void Update(IReadOnlyList<Vector3> centers, int maxGenerate, int maxMesh, double timeBudgetMs = double.MaxValue)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _centers.Clear();
        foreach (var c in centers) _centers.Add(new Point((int)MathF.Floor(c.X) >> 4, (int)MathF.Floor(c.Z) >> 4));

        if (_workers == null) UpdateInline(maxGenerate, maxMesh, timeBudgetMs, clock);
        else UpdateWithWorkers(maxGenerate, maxMesh, timeBudgetMs, clock);

        if (AutoUnloads) Unload();
    }

    /// <summary>Every chunk we want loaded, nearest to any of the centres first.</summary>
    private IEnumerable<Point> WantedChunks()
    {
        foreach (var o in _offsets)
            foreach (var c in _centers)
                yield return new Point(c.X + o.X, c.Y + o.Y);
    }

    private bool NearACenter(int chunkX, int chunkZ, int distance)
    {
        foreach (var c in _centers)
            if (Math.Abs(chunkX - c.X) <= distance && Math.Abs(chunkZ - c.Y) <= distance) return true;
        return false;
    }

    /// <summary>One thread does everything: simple and deterministic, used by tests.</summary>
    private void UpdateInline(int maxGenerate, int maxMesh, double timeBudgetMs, System.Diagnostics.Stopwatch clock)
    {
        if (Generates)
        {
            int generated = 0;
            foreach (var key in WantedChunks())
            {
                if (generated >= maxGenerate || (generated > 0 && clock.Elapsed.TotalMilliseconds > timeBudgetMs)) break;
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
        }

        if (!Meshes || _centers.Count == 0) return;
        MeshUrgent(clock, timeBudgetMs);

        int meshed = 0;
        int rd2 = RenderDistance * RenderDistance;
        foreach (var o in _offsets)
        {
            if (meshed >= maxMesh || (meshed > 0 && clock.Elapsed.TotalMilliseconds > timeBudgetMs)) break;
            if (o.X * o.X + o.Y * o.Y > rd2) continue;
            if (!_chunks.TryGetValue(new Point(_centers[0].X + o.X, _centers[0].Y + o.Y), out var chunk)) continue;
            if (!chunk.NeedsMesh || !HasAllNeighbours(chunk)) continue;

            BuildMesh(chunk);
            meshed++;
        }
    }

    /// <summary>
    /// Worker threads generate, decode and mesh; this thread only hands out the jobs, lights new chunks (which edits the
    /// neighbouring chunks, so it can't run beside other work) and uploads finished meshes. maxGenerate and maxMesh limit how
    /// many jobs are handed out per call; timeBudgetMs limits the time spent lighting and uploading.
    /// </summary>
    private void UpdateWithWorkers(int maxGenerate, int maxMesh, double timeBudgetMs, System.Diagnostics.Stopwatch clock)
    {
        int inFlightLimit = _workers.Count * 4;   // enough to keep every thread busy, few enough that stale jobs don't pile up

        // 1. Finished chunks (generated here, or unpacked from the server): insert them and light them.
        int unloadDist = RenderDistance + 4;
        while (_workers.TryTakeGenerated(out var done))
        {
            if (Role == WorldRole.Client)
            {
                _decoding--;
                if (done.Chunk != null) ReplaceChunk(done.Chunk);   // the server decides what to send, so nothing is discarded
            }
            else
            {
                _generating.Remove(new Point(done.ChunkX, done.ChunkZ));
                if (done.Chunk == null) continue;   // the worker failed; the chunk is simply requested again below
                if (!NearACenter(done.ChunkX, done.ChunkZ, unloadDist)) continue;   // the player has moved away
                if (_chunks.ContainsKey(new Point(done.ChunkX, done.ChunkZ))) continue;
                AddChunk(done.Chunk);
            }
            if (clock.Elapsed.TotalMilliseconds > timeBudgetMs) break;
        }

        // 2. Ask for chunks that are missing, nearest first.
        if (Generates)
        {
            int requested = 0;
            foreach (var key in WantedChunks())
            {
                if (requested >= maxGenerate || _generating.Count >= inFlightLimit) break;
                if (_chunks.ContainsKey(key) || !_generating.Add(key)) continue;
                _workers.RequestGenerate(key.X, key.Y);
                requested++;
            }
        }

        if (!Meshes || _centers.Count == 0) return;

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
            if (!_chunks.TryGetValue(new Point(_centers[0].X + o.X, _centers[0].Y + o.Y), out var chunk)) continue;
            if (!chunk.NeedsMesh || chunk.MeshInFlight) continue;

            var neighbourhood = ChunkNeighborhood.Capture(this, chunk);
            if (neighbourhood == null) continue;

            chunk.MeshInFlight = true;
            _meshesInFlight++;
            queued++;
            _workers.RequestMesh(new MeshRequest { Chunk = chunk, Neighborhood = neighbourhood, Version = chunk.Version });
        }
    }

    /// <summary>Inserts a freshly generated chunk and lights it (unless this is a server, which has no use for light).</summary>
    private void AddChunk(Chunk chunk)
    {
        _chunks[new Point(chunk.ChunkX, chunk.ChunkZ)] = chunk;
        if (Lights) Lighting.InitializeChunk(chunk);
    }

    /// <summary>Puts a chunk in place, replacing any older copy of the same one.</summary>
    private void ReplaceChunk(Chunk chunk)
    {
        if (_chunks.Remove(new Point(chunk.ChunkX, chunk.ChunkZ), out var old)) { old.Dispose(); MeshRevision++; }
        AddChunk(chunk);
    }

    // ---- For a client: chunks arrive from the server ----

    /// <summary>A chunk arrived from the server (see ChunkCodec). It is unpacked, lit and meshed in the background.</summary>
    public void ReceiveChunkData(int chunkX, int chunkZ, byte[] data)
    {
        if (_workers != null)
        {
            _decoding++;
            _workers.RequestDecode(chunkX, chunkZ, data);
            return;
        }
        try { ReplaceChunk(ChunkCodec.Decode(chunkX, chunkZ, data)); }
        catch (InvalidDataException e) { Console.WriteLine(e.Message); }
    }

    /// <summary>The server stopped sending this chunk: drop it (a client never saves chunks).</summary>
    public void ForgetChunk(int chunkX, int chunkZ)
    {
        if (!_chunks.Remove(new Point(chunkX, chunkZ), out var chunk)) return;
        chunk.Dispose();
        MeshRevision++;
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

    /// <summary>Unloads chunks that are well outside the loaded area (of every centre), saving any with changes.</summary>
    private void Unload()
    {
        int unloadDist = RenderDistance + 4;
        _unloadScratch.Clear();
        foreach (var (key, _) in _chunks)
            if (!NearACenter(key.X, key.Y, unloadDist))
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
            if (WorkInFlight == 0 && CountPendingWork(center) == 0) break;
            Thread.Sleep(1);
        }
    }

    /// <summary>Chunks being generated, unpacked or meshed by worker threads right now.</summary>
    public int WorkInFlight => _generating.Count + _meshesInFlight + _decoding;

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

    /// <summary>
    /// How much loading work is left around a position: chunks not yet here plus meshes not yet built. On a client chunks
    /// come from the server, so only chunks that have arrived (and are being unpacked or meshed) count.
    /// </summary>
    public int CountPendingWork(Vector3 center)
    {
        int cx = (int)MathF.Floor(center.X) >> 4, cz = (int)MathF.Floor(center.Z) >> 4;
        int pending = Role == WorldRole.Client ? _decoding : 0, rd2 = RenderDistance * RenderDistance;
        foreach (var o in _offsets)
        {
            if (!_chunks.TryGetValue(new Point(cx + o.X, cz + o.Y), out var chunk))
            {
                if (Role != WorldRole.Client) pending++;
                continue;
            }
            if (Meshes && o.X * o.X + o.Y * o.Y <= rd2 && (chunk.NeedsMesh || chunk.MeshInFlight) && HasAllNeighbours(chunk)) pending++;
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
