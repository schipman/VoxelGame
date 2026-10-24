using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Terrain;

if (args.Length > 0 && args[0] == "all") return RunAll.Run();
if (args.Length > 0 && args[0] == "persist") return PersistTest.Run() + PersistFluidTest.Run();
if (args.Length > 0 && args[0] == "borders") return BorderTest.Run();
if (args.Length > 0 && args[0] == "fluids") return FluidTest.Run();
if (args.Length > 0 && args[0] == "net") return NetTest.Run();
if (args.Length > 0 && args[0] == "server") return ServerTest.Run();
if (args.Length > 0 && args[0] == "threads") return ThreadTest.Run(args.Length > 1 ? int.Parse(args[1]) : 7);
if (args.Length > 0 && args[0] == "cost") return CostTest.Run(args.Length > 1 ? int.Parse(args[1]) : 424242);
if (args.Length > 0 && args[0] == "items") return ItemTest.Run();
if (args.Length > 0 && args[0] == "stats") return StatsTest.Run(args.Length > 1 ? int.Parse(args[1]) : 1);
if (args.Length > 0 && args[0] == "timing") return RiverTiming.Run();
if (args.Length > 0 && args[0] == "fall") return DebugFall.Run();
if (args.Length > 0 && args[0] == "rivers") return RiverTest.Run(args.Length > 1 ? int.Parse(args[1]) : 1);

int seed = args.Length > 0 ? int.Parse(args[0]) : 99;
var gen0 = new WorldGenerator(seed);
var at = gen0.FindBiome(Biome.Beach, 0, 0) ?? (0, 0);   // a coast, so the test has water in it
var centre = new Vector3(at.Item1, 60, at.Item2);
var world = new World(null, seed, 3);
world.Update(centre, int.MaxValue, 0);
Console.WriteLine($"seed {seed}: chunks loaded: {world.Chunks.Count}");

static int SkyBelow(bool full, int above, BlockType cell) => full && !BlockRegistry.DimsSkylight(cell) ? 15 : Math.Max(above - 1 - BlockRegistry.LightAbsorb(cell), 0);

int Validate(string label)
{
    int bad = 0, cells = 0, leafCells = 0;
    var dirs = new[] { (1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1) };
    foreach (var c in world.Chunks.ToList())
    {
        bool interior = true;
        for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
            if (!world.TryGetChunk(c.ChunkX + dx, c.ChunkZ + dz, out _)) interior = false;
        if (!interior) continue;

        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++)
        {
            int wx = c.WorldX + x, wz = c.WorldZ + z;
            for (int y = 0; y < Chunk.Height; y++)
            {
                var type = c.Get(x, y, z);
                bool opaque = BlockRegistry.IsOpaque(type);
                int expSky = 0, expBlk = BlockRegistry.Get(type).LightEmission;
                if (!opaque)
                {
                    if (y == Chunk.Height - 1) expSky = SkyBelow(true, 15, type);   // open sky above the world
                    foreach (var (dx, dy, dz) in dirs)
                    {
                        int ny = y + dy; if (ny < 0 || ny >= Chunk.Height) continue;
                        world.GetCell(wx + dx, ny, wz + dz, out _, out int ns, out int nb);
                        expSky = Math.Max(expSky, dy == 1 ? SkyBelow(ns == 15, ns, type) : Math.Max(ns - 1 - BlockRegistry.LightAbsorb(type), 0));
                        expBlk = Math.Max(expBlk, Math.Max(nb - 1 - BlockRegistry.LightAbsorb(type), 0));
                    }
                }
                int sky = c.GetSkyLight(x, y, z), blk = c.GetBlockLight(x, y, z);
                cells++; if (BlockRegistry.DimsSkylight(type)) leafCells++;
                if (sky != expSky || blk != expBlk)
                    if (bad++ < 5) Console.WriteLine($"  MISMATCH at ({wx},{y},{wz}) {type}: sky {sky} (expected {expSky}), block {blk} (expected {expBlk})");
            }
        }
    }
    Console.WriteLine($"{label}: {cells} cells ({leafCells} leaf cells), {bad} mismatches");
    return bad;
}

int total = Validate("after generation (forests, taiga, ...)");

var rng = new Random(5);
var types = new[] { BlockType.Air, BlockType.Stone, BlockType.Lamp, BlockType.DimLamp, BlockType.OakLeaves, BlockType.OakLeaves,
                    BlockType.SpruceLeaves, BlockType.OakLog, BlockType.Dirt, BlockType.Water, BlockType.Water, BlockType.Air, BlockType.Air, BlockType.Air };
for (int batch = 1; batch <= 40; batch++)
{
    for (int i = 0; i < 25; i++)
    {
        int x = at.Item1 + rng.Next(-24, 26), z = at.Item2 + rng.Next(-24, 26);
        int y = world.Generator.GetHeight(x, z) + rng.Next(-5, 12);   // reaches up into tree canopies
        world.SetBlock(x, y, z, types[rng.Next(types.Length)]);
    }
    for (int t = 0; t < 4; t++) world.Fluids.Step();   // let any water placed above run
    if (batch % 10 == 0) total += Validate($"after edit batch {batch}");
}
// the very top of the world, where light comes from open sky instead of a neighbour
int tx = at.Item1 + 3, tz = at.Item2 + 3;
world.SetBlock(tx, 127, tz, BlockType.OakLeaves); world.SetBlock(tx, 127, tz, BlockType.Air); world.SetBlock(tx + 1, 127, tz, BlockType.Stone); world.SetBlock(tx + 1, 127, tz, BlockType.Air);
total += Validate("after top-of-world edits");
Console.WriteLine(total == 0 ? "LIGHTING ALL OK" : $"LIGHTING FAILED: {total} mismatches");
return total == 0 ? 0 : 1;
