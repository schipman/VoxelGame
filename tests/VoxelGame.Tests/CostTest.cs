using System.Diagnostics;
using Microsoft.Xna.Framework;
using VoxelGame.Terrain;

static class CostTest
{
    public static int Run(int seed)
    {
        const int Radius = 9;
        // 1. Terrain generation alone (blocks, trees, rivers; no light).
        var gen = new WorldGenerator(seed);
        gen.Generate(new Chunk(0, 0));   // warm up (JIT, river regions)
        var sw = Stopwatch.StartNew();
        int count = 0;
        for (int x = -Radius; x <= Radius; x++) for (int z = -Radius; z <= Radius; z++) { gen.Generate(new Chunk(x, z)); count++; }
        double genMs = sw.Elapsed.TotalMilliseconds;

        // 2. Generation + lighting together, through the world (so lighting can reach neighbours).
        var world = new World(null, seed, Radius - 2);
        sw.Restart();
        world.Update(Vector3.Zero, int.MaxValue, 0);
        double genLightMs = sw.Elapsed.TotalMilliseconds;
        int loaded = world.Chunks.Count;

        // 3. Meshing every chunk that has all its neighbours.
        sw.Restart();
        int meshed = 0;
        foreach (var c in world.Chunks.ToList()) if (world.BuildMesh(c)) meshed++;
        double meshMs = sw.Elapsed.TotalMilliseconds;

        // 4. Mesh size.
        var mesher = new VoxelGame.Rendering.ChunkMesher();
        long verts = 0, tris = 0, wverts = 0;
        foreach (var c in world.Chunks)
        {
            var nb = ChunkNeighborhood.Capture(world, c);
            if (nb == null) continue;
            var solid = mesher.Build(nb, out var water);
            verts += solid.Vertices.Length; tris += solid.Indices.Length / 3; wverts += water.Vertices.Length;
        }

        Console.WriteLine($"generate only:        {genMs / count,6:F2} ms/chunk  ({count} chunks)");
        Console.WriteLine($"generate + light:     {genLightMs / loaded,6:F2} ms/chunk  ({loaded} chunks)  => light init ~{(genLightMs - genMs * loaded / count) / loaded:F2} ms/chunk");
        Console.WriteLine($"mesh (build only):    {meshMs / meshed,6:F2} ms/chunk  ({meshed} chunks)");
        Console.WriteLine($"mesh size:            {verts / meshed,6} verts, {tris / meshed,6} tris, {wverts / meshed,5} water verts per chunk");
        return 0;
    }
}
