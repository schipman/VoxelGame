using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Terrain;

/// <summary>Checks the flow simulation on a stone platform high above the terrain, where nothing else interferes.</summary>
static class FluidTest
{
    const int Floor = 110, Size = 31;   // platform: Size x Size blocks at y = Floor

    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }

    static void Settle(World w, int maxTicks = 1500)
    {
        int ticks = 0;
        while (w.Fluids.PendingCount > 0 && ticks++ < maxTicks) w.Fluids.Step();
    }

    // Builds a fresh platform with stone below y=Floor+1 and clears everything above, in a 31x31 area around the origin.
    static World NewWorld(out int x0, out int z0)
    {
        var w = new World(null, 5, 2);
        w.Update(Vector3.Zero, int.MaxValue, 0);
        x0 = -Size / 2; z0 = -Size / 2;
        for (int x = x0; x < x0 + Size; x++)
        for (int z = z0; z < z0 + Size; z++)
        {
            for (int y = Floor - 3; y <= Floor + 14; y++) w.SetBlock(x, y, z, y <= Floor ? BlockType.Stone : BlockType.Air);
        }
        Settle(w);
        return w;
    }

    public static int Run()
    {
        // ---- A: a lone source on flat ground spreads into a diamond: level = distance, out to 7 ----
        var w = NewWorld(out int x0, out int z0);
        w.SetBlock(0, Floor + 1, 0, BlockType.Water);     // source
        Settle(w);
        int wrongA = 0, waterCells = 0;
        for (int x = x0; x < x0 + Size; x++)
        for (int z = z0; z < z0 + Size; z++)
        {
            int d = Math.Abs(x) + Math.Abs(z);
            bool isWater = w.IsWater(x, Floor + 1, z);
            bool expected = d <= 7;
            if (isWater != expected) wrongA++;
            else if (isWater) { waterCells++; if (w.GetFluidLevel(x, Floor + 1, z) != d) wrongA++; }
        }
        Check(wrongA == 0 && waterCells == 2 * 7 * 8 + 1, $"A: source spreads into a diamond (level = distance up to 7); {waterCells} cells, {wrongA} wrong");

        // ---- B: take the source away and everything dries up ----
        w.SetBlock(0, Floor + 1, 0, BlockType.Air);
        Settle(w);
        int left = 0;
        for (int x = x0; x < x0 + Size; x++) for (int z = z0; z < z0 + Size; z++) if (w.IsWater(x, Floor + 1, z)) left++;
        Check(left == 0, $"B: removing the source drains all {waterCells} flowing cells ({left} left)");

        // ---- C: a source in the air over the floor makes a waterfall, then a pool ----
        w = NewWorld(out x0, out z0);
        w.SetBlock(0, Floor + 9, 0, BlockType.Water);
        Settle(w);
        bool column = true;
        for (int y = Floor + 1; y < Floor + 9; y++) column &= w.IsWater(0, y, 0) && w.GetFluidLevel(0, y, 0) == 1;
        Check(column, "C: a source in mid-air pours a column of water to the floor");
        Check(w.IsWater(1, Floor + 1, 0) && w.GetFluidLevel(1, Floor + 1, 0) == 2 && w.IsWater(6, Floor + 1, 0) && !w.IsWater(7, Floor + 1, 0),
              "C: the waterfall spreads into a pool (level 2 beside it, out to 6 blocks)");
        w.SetBlock(0, Floor + 9, 0, BlockType.Air);
        Settle(w);
        int remaining = 0;
        for (int x = x0; x < x0 + Size; x++) for (int z = z0; z < z0 + Size; z++) for (int y = Floor + 1; y <= Floor + 12; y++) if (w.IsWater(x, y, z)) remaining++;
        Check(remaining == 0, $"C: removing the top source drains the fall and the pool ({remaining} left)");

        // ---- D: walls hold water back; a gap lets it through; placing a block stops it ----
        w = NewWorld(out x0, out z0);
        for (int z = z0; z < z0 + Size; z++) { w.SetBlock(3, Floor + 1, z, BlockType.Stone); }   // wall at x = 3
        w.SetBlock(0, Floor + 1, 0, BlockType.Water);
        Settle(w);
        bool leaked = false;
        for (int z = z0; z < z0 + Size; z++) for (int x = 4; x < 12; x++) leaked |= w.IsWater(x, Floor + 1, z);
        Check(!leaked && w.IsWater(2, Floor + 1, 0), "D: a wall holds the water back");
        w.SetBlock(3, Floor + 1, 0, BlockType.Air);   // knock a hole in it
        Settle(w);
        Check(w.IsWater(3, Floor + 1, 0) && w.IsWater(4, Floor + 1, 0), "D: knocking a hole lets water through");
        w.SetBlock(3, Floor + 1, 0, BlockType.Stone);  // plug it
        Settle(w);
        Check(!w.IsWater(4, Floor + 1, 0), "D: plugging the hole dries up the far side");

        // ---- E: invariants hold over everything that exists, after a mixed series of edits ----
        w = NewWorld(out x0, out z0);
        var rng = new Random(9);
        for (int i = 0; i < 80; i++)
        {
            int x = rng.Next(x0 + 2, x0 + Size - 2), z = rng.Next(z0 + 2, z0 + Size - 2), y = Floor + 1 + rng.Next(0, 4);
            var type = rng.Next(5) switch { 0 => BlockType.Water, 1 => BlockType.Stone, 2 => BlockType.Air, 3 => BlockType.Air, _ => BlockType.Dirt };
            w.SetBlock(x, y, z, type);
            if (i % 8 == 0) for (int t = 0; t < 6; t++) w.Fluids.Step();
        }
        Settle(w);
        int unfed = 0, tooHigh = 0, floating = 0, leakingSideways = 0, total = 0;
        for (int x = x0 + 1; x < x0 + Size - 1; x++)
        for (int z = z0 + 1; z < z0 + Size - 1; z++)
        for (int y = Floor + 1; y <= Floor + 14; y++)
        {
            if (!w.IsWater(x, y, z)) continue;
            total++;
            int level = w.GetFluidLevel(x, y, z);
            if (level > FluidSimulator.MaxLevel) tooHigh++;
            if (level > 0)
            {
                bool fed = w.IsWater(x, y + 1, z);
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    fed |= w.IsWater(x + dx, y, z + dz) && w.GetFluidLevel(x + dx, y, z + dz) < level;
                if (!fed) unfed++;
            }
            var below = w.GetBlock(x, y - 1, z);
            if (below == BlockType.Air) floating++;                                  // water hanging over air should have fallen
            else if (level < FluidSimulator.MaxLevel && !BlockRegistry.IsLiquid(below))
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    if (w.GetBlock(x + dx, y, z + dz) == BlockType.Air) leakingSideways++;   // should have spread there
        }
        Check(tooHigh == 0 && unfed == 0 && floating == 0 && leakingSideways == 0,
              $"E: after 80 random edits, {total} water cells all obey the rules (too high {tooHigh}, unfed {unfed}, floating {floating}, un-spread {leakingSideways})");

        Console.WriteLine(failures == 0 ? "FLUIDS ALL OK" : $"FLUIDS FAILED: {failures}");
        return failures;
    }
}
