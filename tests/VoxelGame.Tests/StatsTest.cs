using VoxelGame.Terrain;
static class StatsTest
{
    public static int Run(int seed)
    {
        var g = new WorldGenerator(seed);
        const int Span = 12000, Step = 48;
        var counts = new Dictionary<Biome, int>(); int n = 0, water = 0;
        var depths = new List<int>(); var oceanBiomeDepths = new List<int>();
        for (int x = -Span; x < Span; x += Step) for (int z = -Span; z < Span; z += Step)
        {
            var b = g.GetBiomeAt(x, z); counts[b] = counts.GetValueOrDefault(b) + 1; n++;
            if (g.HasWater(x, z)) { water++; int d = WorldGenerator.SeaLevel - g.GetHeight(x, z); depths.Add(d); if (b == Biome.Ocean) oceanBiomeDepths.Add(d); }
        }
        depths.Sort(); oceanBiomeDepths.Sort();
        Console.WriteLine($"seed {seed}: {100.0 * water / n:F1}% of columns have water");
        if (oceanBiomeDepths.Count > 0) Console.WriteLine($"  ocean depth below sea level: median {oceanBiomeDepths[oceanBiomeDepths.Count / 2]}, p90 {oceanBiomeDepths[(int)(oceanBiomeDepths.Count * .9)]}, max {oceanBiomeDepths[^1]}");
        Console.WriteLine("  " + string.Join("  ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {100.0 * kv.Value / n:F1}%")));
        return 0;
    }
}
