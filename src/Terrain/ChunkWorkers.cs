using System.Collections.Concurrent;
using VoxelGame.Persistence;
using VoxelGame.Rendering;

namespace VoxelGame.Terrain;

/// <summary>A request to build a chunk's meshes from a snapshot of it and its neighbours. The worker fills in the results.</summary>
public sealed class MeshRequest
{
    public Chunk Chunk;
    public ChunkNeighborhood Neighborhood;

    /// <summary>The chunk's <see cref="Terrain.Chunk.Version"/> when the snapshot was taken.</summary>
    public int Version;

    public ChunkMeshData Solid, Water;
}

/// <summary>The outcome of a generation request: the chunk, or null if it failed.</summary>
public sealed class GenerateResult
{
    public int ChunkX, ChunkZ;
    public Chunk Chunk;
}

/// <summary>
/// A small pool of background threads that do the heavy, self-contained chunk work so the game loop doesn't have to:
///   - generating a chunk's blocks (from the seed, or from a save file),
///   - building a chunk's mesh from a snapshot of it and its neighbours.
/// Results come back through queues that the main thread drains; anything that touches the shared world (lighting,
/// inserting chunks, uploading to the GPU) stays on the main thread. Meshing is preferred over generating, so what is
/// already loaded becomes visible first.
/// </summary>
public sealed class ChunkWorkers : IDisposable
{
    private readonly Thread[] _threads;
    private readonly WorldGenerator _generator;
    private readonly WorldStorage _storage;

    private readonly ConcurrentQueue<(int X, int Z)> _generateQueue = new();
    private readonly ConcurrentQueue<MeshRequest> _meshQueue = new();
    private readonly ConcurrentQueue<GenerateResult> _generated = new();
    private readonly ConcurrentQueue<MeshRequest> _meshed = new();
    private readonly SemaphoreSlim _work = new(0);
    private volatile bool _stop;

    public int Count => _threads.Length;

    public ChunkWorkers(int threads, WorldGenerator generator, WorldStorage storage)
    {
        _generator = generator;
        _storage = storage;
        _threads = new Thread[Math.Max(1, threads)];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Run) { IsBackground = true, Name = $"chunk-worker-{i}", Priority = ThreadPriority.BelowNormal };
            _threads[i].Start();
        }
    }

    public void RequestGenerate(int chunkX, int chunkZ)
    {
        _generateQueue.Enqueue((chunkX, chunkZ));
        _work.Release();
    }

    public void RequestMesh(MeshRequest request)
    {
        _meshQueue.Enqueue(request);
        _work.Release();
    }

    public bool TryTakeGenerated(out GenerateResult result) => _generated.TryDequeue(out result);
    public bool TryTakeMeshed(out MeshRequest result) => _meshed.TryDequeue(out result);

    private void Run()
    {
        var mesher = new ChunkMesher();   // reuses its buffers between chunks, so each thread has its own

        while (true)
        {
            _work.Wait();
            if (_stop) return;

            try
            {
                if (_meshQueue.TryDequeue(out var request))
                {
                    request.Solid = mesher.Build(request.Neighborhood, out request.Water);
                    _meshed.Enqueue(request);
                }
                else if (_generateQueue.TryDequeue(out var at))
                {
                    _generated.Enqueue(new GenerateResult { ChunkX = at.X, ChunkZ = at.Z, Chunk = GenerateChunk(at.X, at.Z) });
                }
            }
            catch (Exception e)
            {
                // Never let a bug kill a worker silently: report it. The world notices the missing result and retries later.
                Console.WriteLine($"Chunk worker error: {e}");
            }
        }
    }

    /// <summary>A chunk the player edited comes from disk; anything else is generated from the seed.</summary>
    private Chunk GenerateChunk(int chunkX, int chunkZ)
    {
        var chunk = new Chunk(chunkX, chunkZ);
        if (_storage != null && _storage.TryLoadChunk(chunk))
            _generator.FillRiverFlow(chunk);   // rivers are not saved, only the blocks
        else
            _generator.Generate(chunk);
        return chunk;
    }

    public void Dispose()
    {
        _stop = true;
        _work.Release(_threads.Length);
        foreach (var thread in _threads) thread.Join(2000);
    }
}
