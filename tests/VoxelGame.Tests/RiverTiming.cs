using System.Diagnostics;
using VoxelGame.Terrain;
static class RiverTiming
{
    public static int Run()
    {
        var gen = new WorldGenerator(1);
        var sw = Stopwatch.StartNew();
        gen.GetHeight(0, 0);
        Console.WriteLine($"first query (traces 25 regions): {sw.ElapsedMilliseconds} ms");
        for (int i = 1; i <= 4; i++)
        {
            sw.Restart();
            gen.GetHeight(i * 1536, 0);   // walk one region east: 5 new regions to trace
            Console.WriteLine($"step {i} east: {sw.ElapsedMilliseconds} ms");
        }
        sw.Restart();
        for (int i = 0; i < 20000; i++) gen.GetHeight(6000 + i % 150, 100 + i / 150);
        Console.WriteLine($"20000 column samples in an already-traced area: {sw.ElapsedMilliseconds} ms");
        return 0;
    }
}
