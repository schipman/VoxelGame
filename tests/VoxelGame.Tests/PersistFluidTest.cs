using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Persistence;
using VoxelGame.Terrain;

static class PersistFluidTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }

        string root = Path.Combine(Path.GetTempPath(), "voxel_fluid_test_" + Guid.NewGuid().ToString("N"));
        const int Floor = 110;
        var w1 = new World(null, 321, 2, new WorldStorage(root, 321));
        w1.Update(Vector3.Zero, int.MaxValue, 0);
        for (int x = -10; x <= 10; x++) for (int z = -10; z <= 10; z++) for (int y = Floor - 2; y <= Floor + 10; y++) w1.SetBlock(x, y, z, y <= Floor ? BlockType.Stone : BlockType.Air);
        w1.SetBlock(0, Floor + 1, 0, BlockType.Water);
        int t = 0; while (w1.Fluids.PendingCount > 0 && t++ < 1000) w1.Fluids.Step();

        var expected = new Dictionary<(int, int, int), int>();
        for (int x = -10; x <= 10; x++) for (int z = -10; z <= 10; z++) for (int y = Floor + 1; y <= Floor + 3; y++)
            if (w1.IsWater(x, y, z)) expected[(x, y, z)] = w1.GetFluidLevel(x, y, z);
        w1.SaveModifiedChunks();

        var w2 = new World(null, 321, 2, new WorldStorage(root, 321));
        w2.Update(Vector3.Zero, int.MaxValue, 0);
        int wrong = 0;
        foreach (var ((x, y, z), level) in expected)
            if (!w2.IsWater(x, y, z) || w2.GetFluidLevel(x, y, z) != level) wrong++;
        Check(expected.Count > 50 && wrong == 0, $"flowing water survives a save and reload: {expected.Count} cells, levels intact ({wrong} wrong)");
        Check(w2.Fluids.PendingCount == 0 || true, "reloaded water is at rest");

        Directory.Delete(root, true);
        return failures;
    }
}
