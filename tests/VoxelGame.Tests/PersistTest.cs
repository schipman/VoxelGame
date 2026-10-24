using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Persistence;
using VoxelGame.Terrain;

static class PersistTest
{
    public static int Run()
    {
        
        string root = Path.Combine(Path.GetTempPath(), "voxel_save_test_" + Guid.NewGuid().ToString("N"));
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }
        
        // --- 1. edit, save, reload in a brand-new world ---
        var storage = new WorldStorage(root, 4242);
        var w1 = new World(null, 4242, 3, storage);
        w1.Update(Vector3.Zero, int.MaxValue, 0);
        int h = w1.Generator.GetHeight(3, 5);
        var untouchedBefore = w1.GetBlock(40, w1.Generator.GetHeight(40, 40), 40);
        
        w1.SetBlock(3, h, 5, BlockType.Air);                 // dig
        w1.SetBlock(3, h + 1, 5, BlockType.Lamp);            // build
        w1.SetBlock(-1, h, -1, BlockType.DimLamp);           // chunk corner (4 chunks meet)
        Check(w1.HasBeenModified, "world reports it was modified");
        int saved = w1.SaveModifiedChunks();
        Check(saved == 2 || saved == 1 || saved >= 1, $"saved {saved} chunk(s)");
        Check(w1.SaveModifiedChunks() == 0, "nothing left to save afterwards");
        var files = Directory.GetFiles(Path.Combine(storage.Directory, "chunks"), "*.bin");
        Console.WriteLine($"      {files.Length} chunk files, sizes: {string.Join(", ", files.Select(f => new FileInfo(f).Length + " B"))}");
        
        var w2 = new World(null, 4242, 3, new WorldStorage(root, 4242));
        w2.Update(Vector3.Zero, int.MaxValue, 0);
        Check(w2.GetBlock(3, h, 5) == BlockType.Air, "dug block is still air after reload");
        Check(w2.GetBlock(3, h + 1, 5) == BlockType.Lamp, "placed lamp is still there after reload");
        Check(w2.GetBlock(-1, h, -1) == BlockType.DimLamp, "block at chunk corner survived");
        Check(w2.GetBlock(40, w2.Generator.GetHeight(40, 40), 40) == untouchedBefore, "untouched terrain regenerates identically");
        
        // lighting is rebuilt from the blocks on load
        w2.GetCell(3, h + 2, 5, out _, out _, out int lampLight);
        Check(lampLight == 14, $"lamp light rebuilt on reload (cell above lamp has block light {lampLight})");
        
        // --- 2. save-on-unload: edit, walk far away so the chunk unloads, come back ---
        w2.SetBlock(1, h + 5, 1, BlockType.Stone);
        w2.Update(new Vector3(2000, 60, 2000), int.MaxValue, 0);   // far away: chunks near origin unload
        Check(!w2.TryGetChunk(0, 0, out _), "origin chunk was unloaded");
        w2.Update(Vector3.Zero, int.MaxValue, 0);
        Check(w2.GetBlock(1, h + 5, 1) == BlockType.Stone, "edit survived unload and reload");
        
        // --- 3. a world nobody edited is not written, level file round-trips ---
        var s3 = new WorldStorage(root, 777);
        var w3 = new World(null, 777, 2, s3);
        w3.Update(Vector3.Zero, int.MaxValue, 0);
        Check(!w3.HasBeenModified && w3.SaveModifiedChunks() == 0 && !Directory.Exists(s3.Directory), "unedited world writes nothing");
        s3.SaveLevel(new LevelData { Seed = 777, TimeOfDay = 0.6f, PlayerX = 1.5f, PlayerY = 70, PlayerZ = -3, Yaw = 1.2f, Pitch = -0.3f, Flying = true });
        var lv = s3.LoadLevel();
        Check(lv is { Seed: 777, Flying: true } && Math.Abs(lv.TimeOfDay - 0.6f) < 1e-6 && lv.PlayerY == 70, "level.json round-trips");
        
        // --- 4. corrupt file falls back to generation and is kept aside ---
        string bad = Path.Combine(storage.Directory, "chunks", "c.5.5.bin");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5 });
        var w4 = new World(null, 4242, 3, new WorldStorage(root, 4242));
        w4.Update(new Vector3(5 * 16, 60, 5 * 16), int.MaxValue, 0);
        Check(w4.TryGetChunk(5, 5, out var c55) && c55.Get(0, 0, 0) == BlockType.Bedrock, "corrupt chunk regenerated from seed");
        Check(File.Exists(bad + ".corrupt") && !File.Exists(bad), "corrupt file moved aside");
        
        Directory.Delete(root, true);
        Console.WriteLine(failures == 0 ? "PERSISTENCE ALL OK" : $"{failures} FAILURE(S)");
        return failures;
    }
}
