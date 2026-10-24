using System.Diagnostics;
using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Rendering;
using VoxelGame.Terrain;

/// <summary>Checks that generating and meshing on worker threads gives exactly the same world as doing it on one thread.</summary>
static class ThreadTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }

    static ulong Hash(ChunkMeshData m)
    {
        ulong h = 1469598103934665603UL;
        foreach (var v in m.Vertices)
        {
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.Position.X)) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.Position.Y)) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.Position.Z)) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.TexCoord.X)) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(v.TexCoord.Y)) * 1099511628211UL;
            h = (h ^ v.Light.PackedValue) * 1099511628211UL;
            h = (h ^ v.Extra.PackedValue) * 1099511628211UL;
        }
        foreach (var i in m.Indices) h = (h ^ (uint)i) * 1099511628211UL;
        return h;
    }

    static ulong Hash(Chunk c)
    {
        ulong h = 1469598103934665603UL;
        for (int y = 0; y < Chunk.Height; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
        {
            h = (h ^ (byte)c.Get(x, y, z)) * 1099511628211UL;
            h = (h ^ (uint)c.GetSkyLight(x, y, z)) * 1099511628211UL;
            h = (h ^ (uint)c.GetBlockLight(x, y, z)) * 1099511628211UL;
            h = (h ^ (uint)c.GetFluidLevel(x, y, z)) * 1099511628211UL;
        }
        return h;
    }

    /// <summary>Runs updates until nothing is loading or meshing.</summary>
    static void Settle(World w, Vector3 centre)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 60)
        {
            w.Update(centre, 64, 64);
            if (w.WorkInFlight == 0 && w.CountPendingWork(centre) == 0) return;
            Thread.Sleep(1);
        }
        Console.WriteLine("  (timed out settling)");
    }

    public static int Run(int seed)
    {
        const int RD = 5;
        var centre = new Vector3(100, 60, -80);

        // ---- A: one thread. ----
        var meshesA = new Dictionary<(int, int), ulong>();
        var a = new World(null, seed, RD);
        a.MeshApplied += (c, solid, water, v) => meshesA[(c.ChunkX, c.ChunkZ)] = Hash(solid) ^ (Hash(water) * 31);
        var sw = Stopwatch.StartNew();
        a.Update(centre, int.MaxValue, int.MaxValue);
        double inlineMs = sw.Elapsed.TotalMilliseconds;

        // ---- B: worker threads. ----
        var meshesB = new Dictionary<(int, int), ulong>();
        var b = new World(null, seed, RD, null, workerThreads: 4);
        b.MeshApplied += (c, solid, water, v) => meshesB[(c.ChunkX, c.ChunkZ)] = Hash(solid) ^ (Hash(water) * 31);
        sw.Restart();
        b.LoadAll(centre);
        double threadedMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"      {a.Chunks.Count} chunks: one thread {inlineMs:F0} ms, four workers {threadedMs:F0} ms ({inlineMs / threadedMs:F1}x)");

        Check(a.Chunks.Count == b.Chunks.Count, $"both worlds loaded the same number of chunks ({a.Chunks.Count})");
        int blockMismatch = 0;
        foreach (var ca in a.Chunks)
            if (!b.TryGetChunk(ca.ChunkX, ca.ChunkZ, out var cb) || Hash(ca) != Hash(cb)) blockMismatch++;
        Check(blockMismatch == 0, $"every chunk's blocks, light and water are identical ({blockMismatch} differ)");

        int meshMismatch = meshesA.Count(kv => !meshesB.TryGetValue(kv.Key, out var h) || h != kv.Value);
        Check(meshesA.Count > 20 && meshMismatch == 0 && meshesA.Count == meshesB.Count, $"every mesh built by a worker equals the single-threaded one ({meshesA.Count} meshes, {meshMismatch} differ)");

        // ---- C: edit storm while workers are busy, then everything must end up consistent. ----
        var rng = new Random(11);
        var latest = new Dictionary<(int, int), (ulong Hash, int Version)>();
        b.MeshApplied += (c, solid, water, v) => latest[(c.ChunkX, c.ChunkZ)] = (Hash(solid) ^ (Hash(water) * 31), v);
        var types = new[] { BlockType.Air, BlockType.Stone, BlockType.Lamp, BlockType.Dirt, BlockType.Water, BlockType.OakLeaves, BlockType.Air };
        for (int round = 0; round < 60; round++)
        {
            for (int i = 0; i < 12; i++)
            {
                int x = (int)centre.X + rng.Next(-40, 41), z = (int)centre.Z + rng.Next(-40, 41);
                int y = b.Generator.GetHeight(x, z) + rng.Next(-3, 6);
                b.SetBlock(x, y, z, types[rng.Next(types.Length)]);
            }
            b.Update(centre, 64, 64);
            b.Tick(0.2f);
            if (round % 10 == 0) Thread.Sleep(3);
        }
        Settle(b, centre);
        // Water may still be flowing; let it finish, then settle again.
        for (int i = 0; i < 400 && b.Fluids.PendingCount > 0; i++) { b.Fluids.Step(); b.Update(centre, 64, 64); }
        Settle(b, centre);

        int stale = 0, checkedChunks = 0, wrongMesh = 0;
        var mesher = new ChunkMesher();
        foreach (var c in b.Chunks)
        {
            var nb = ChunkNeighborhood.Capture(b, c);
            if (nb == null) continue;
            int dx = c.ChunkX - (int)Math.Floor(centre.X / 16), dz = c.ChunkZ - (int)Math.Floor(centre.Z / 16);
            if (dx * dx + dz * dz > RD * RD) continue;      // chunks outside the render distance are never meshed
            checkedChunks++;
            if (c.NeedsMesh) { stale++; continue; }
            var fresh = mesher.Build(nb, out var freshWater);
            if (!latest.TryGetValue((c.ChunkX, c.ChunkZ), out var last)) continue;   // never remeshed after the edits: untouched by them
            if (last.Hash != (Hash(fresh) ^ (Hash(freshWater) * 31))) wrongMesh++;
        }
        Check(stale == 0, $"after 720 edits, no chunk is left needing a mesh ({stale} of {checkedChunks} stale)");
        Check(wrongMesh == 0, $"the last mesh applied to every edited chunk matches the current blocks ({wrongMesh} out of date)");

        b.Dispose();
        Console.WriteLine(failures == 0 ? "THREADS ALL OK" : $"THREADS FAILED: {failures}");
        return failures;
    }
}
