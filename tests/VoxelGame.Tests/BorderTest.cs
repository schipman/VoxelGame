using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Terrain;

static class BorderTest
{
    public static int Run()
    {
        int failures = 0;
        foreach (var (biome, name) in new[] { (Biome.DarkForest, "dark forest (radius-4 canopies)"), (Biome.Forest, "forest"), (Biome.Taiga, "taiga (spruce cones)") })
        {
            var gen = new WorldGenerator(1);
            var at = gen.FindBiome(biome, 0, 0).Value;
            var world = new World(null, 1, 10);
            world.Update(new Vector3(at.X, 60, at.Z), int.MaxValue, 0);

            var leafByX = new long[16]; var leafByZ = new long[16]; var logByX = new long[16];
            int counted = 0;
            foreach (var c in world.Chunks)
            {
                // only chunks fully inside the biome so density is comparable
                bool inside = true;
                for (int dx = 0; dx <= 16 && inside; dx += 8) for (int dz = 0; dz <= 16 && inside; dz += 8)
                    inside = gen.GetBiome(c.WorldX + dx - (dx == 16 ? 1 : 0), c.WorldZ + dz - (dz == 16 ? 1 : 0)) == biome;
                if (!inside) continue;
                counted++;
                for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) for (int y = 40; y < 120; y++)
                {
                    var t = c.Get(x, y, z);
                    if (TreeBuilder.IsLeaves(t)) { leafByX[x]++; leafByZ[z]++; }
                    if (t is BlockType.OakLog or BlockType.DarkOakLog or BlockType.SpruceLog or BlockType.BirchLog) logByX[x]++;
                }
            }
            // Compare the columns on chunk borders (x or z = 0 and 15) against the ones in the middle. If trees were cut at
            // borders, the border columns would hold clearly fewer leaves; noise affects both groups alike.
            double Mean(long[] a, int[] idx) => idx.Average(i => (double)a[i]);
            int[] edge = { 0, 15 }, middle = Enumerable.Range(2, 12).ToArray();
            double ratioX = Mean(leafByX, edge) / Mean(leafByX, middle), ratioZ = Mean(leafByZ, edge) / Mean(leafByZ, middle);
            Console.WriteLine($"{name}: {counted} chunks; leaves in border columns vs middle columns: x {ratioX:P0}, z {ratioZ:P0}");
            if (ratioX < 0.9 || ratioZ < 0.9) { failures++; Console.WriteLine("  FAIL: border columns have fewer leaves, trees may be cut at chunk borders"); }
        }
        Console.WriteLine(failures == 0 ? "BORDERS OK" : "BORDERS FAILED");
        return failures;
    }
}
