using VoxelGame.Terrain;

static class RiverTest
{
    public static int Run(int seed)
    {
        // Same points, two generators, opposite query orders. If a river's presence depended on which regions had been
        // traced first, the answers would differ.
        var rng = new Random(3);
        var points = Enumerable.Range(0, 6000).Select(_ => (X: rng.Next(-7000, 7000), Z: rng.Next(-7000, 7000))).ToArray();

        var a = new WorldGenerator(seed);
        var forward = points.Select(p => (a.GetHeight(p.X, p.Z), a.GetBiomeAt(p.X, p.Z), a.HasWater(p.X, p.Z))).ToArray();

        var b = new WorldGenerator(seed);
        // Warm b up somewhere completely different first, then query in reverse.
        for (int i = 0; i < 3000; i++) b.GetHeight(rng.Next(-9000, 9000), rng.Next(-9000, 9000));
        var backward = new (int, Biome, bool)[points.Length];
        for (int i = points.Length - 1; i >= 0; i--)
            backward[i] = (b.GetHeight(points[i].X, points[i].Z), b.GetBiomeAt(points[i].X, points[i].Z), b.HasWater(points[i].X, points[i].Z));

        int mismatches = 0;
        for (int i = 0; i < points.Length; i++)
            if (forward[i] != backward[i] && mismatches++ < 5)
                Console.WriteLine($"  MISMATCH at {points[i]}: {forward[i]} vs {backward[i]}");

        int riverPoints = forward.Count(f => f.Item2 == Biome.River);
        var rv = a.Rivers;
        Console.WriteLine($"seed {seed}: {points.Length} points, {riverPoints} on rivers, {mismatches} mismatches; " +
                          $"river traces {rv.TraceCount}, reached sea {rv.ReachedSeaCount}, stuck in basins {rv.GaveUpCount}, ran out of length {rv.PondCount}");
        Console.WriteLine($"  by fork depth (main river / fork / fork of fork): traces {string.Join('/', rv.TracesByDepth)}, reached sea {string.Join('/', rv.SeaByDepth)}");
        Console.WriteLine($"  lakes: {a.Lakes.LakesPlaced} of {a.Lakes.CellsDecided} cells; ponds: {a.Ponds.LakesPlaced} of {a.Ponds.CellsDecided} cells; lake/pond points {forward.Count(f => f.Item2 == Biome.Lake)}");
        // spawn: for a bunch of seeds, is the spawn dry land near water, and how long did finding it take?
        int near = 0, total = 0; long ms = 0;
        for (int sd = 100; sd < 130; sd++)
        {
            var g = new WorldGenerator(sd);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (sx, sz) = g.FindSpawn();
            ms += sw.ElapsedMilliseconds;
            total++;
            bool water = false;
            for (int r = 10; r <= 100 && !water; r += 5)
                for (int i = 0; i < 24 && !water; i++) water = g.HasWater(sx + (int)(Math.Cos(i / 24.0 * 6.2832) * r), sz + (int)(Math.Sin(i / 24.0 * 6.2832) * r));
            if (water && !g.HasWater(sx, sz)) near++;
        }
        Console.WriteLine($"  spawn: {near} of {total} seeds start on dry land with water within 100 blocks; average search {ms / total} ms");
        return mismatches;
    }
}
