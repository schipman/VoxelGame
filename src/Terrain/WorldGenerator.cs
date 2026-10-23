using Microsoft.Xna.Framework;
using VoxelGame.Blocks;

namespace VoxelGame.Terrain;

/// <summary>
/// Turns a seed into terrain, in the spirit of Minecraft's generator:
///   1. Climate: noise fields for continentalness, erosion, weirdness, temperature and humidity (ClimateSampler).
///   2. Biome:   chosen from the climate (BiomeSource).
///   3. Height:  continentalness sets the base level (ocean to inland), erosion sets how rugged it is,
///               and peaks-and-valleys pushes ridges up and valleys down.
///   4. Water:   rivers carved from high ground down to the sea (RiverNetwork), and lakes and ponds scattered over
///               flat inland ground (LakeField), all filled with water.
///   5. Sea:     everything below sea level is filled with water.
///   6. Surface: each biome decides what the top layers are made of (BiomeRegistry), with extra rules for rock and snow.
///   7. Trees:   placed on a jittered grid so they are evenly spaced, and stamped across chunk borders consistently.
/// Everything is a pure function of (seed, x, z), so chunks can be generated in any order.
/// </summary>
public sealed class WorldGenerator
{
    public const int SeaLevel = 46;

    /// <summary>Bump when the generator changes in a way that makes old terrain not match saved edits.</summary>
    public const int Version = 4;

    // Chunks are generated with this many extra columns of context on every side: slopes need a neighbour's
    // height, and trees from the next chunk over can hang into this one.
    private const int Pad = 6;
    private const int CacheSize = Chunk.Size + 2 * Pad;

    private const int TreeCell = 4;   // one tree candidate per 4x4 block cell

    private const int SnowLine = 100;       // mountains above this are snow covered
    private const int SteepSlope = 3;       // height difference to a neighbour that exposes bare rock

    private const float RiverDepth = 2.6f;  // how far below the water the river bed dips in the middle

    private static readonly Curve BaseHeight = new(
        (-1.0f, 12f), (-0.6f, 22f), (-0.32f, 36f), (-0.2f, 45f), (-0.14f, 47.5f), (-0.1f, 49f),
        (0.1f, 54f), (0.4f, 60f), (1.0f, 70f));

    // How rugged the land is: 1 = full mountains, 0 = pancake flat.
    private static readonly Curve Relief = new(
        (-1.0f, 1.0f), (-0.5f, 0.8f), (-0.2f, 0.55f), (0.1f, 0.3f), (0.5f, 0.12f), (1.0f, 0.04f));

    private readonly int _seed;
    private readonly ClimateSampler _climate;
    private readonly PerlinNoise _detail;
    private readonly PerlinNoise _patches;
    private readonly RiverNetwork _rivers;
    private readonly LakeField _lakes;
    private readonly LakeField _ponds;

    /// <summary>The river network, exposed so tests and tools can inspect it.</summary>
    public RiverNetwork Rivers => _rivers;
    public LakeField Lakes => _lakes;
    public LakeField Ponds => _ponds;

    /// <summary>Everything the generator knows about one column of the world.</summary>
    private struct Column
    {
        public Climate Climate;
        public int Height;          // y of the top solid block (after rivers have been carved)
        public int WaterTop;        // y of the top water block; 0 if the column has no water
        public Biome Biome;
        public RiverSample? River;  // the nearby river, if any
        public LakeSample? Lake;    // the nearby lake or pond, if any
    }

    private readonly record struct Surface(BlockType Top, BlockType Filler, int FillerDepth, BlockType Deep, int DeepDepth);

    public WorldGenerator(int seed)
    {
        _seed = seed;
        _climate = new ClimateSampler(seed);
        _detail = new PerlinNoise(seed * 31 + 6);
        _patches = new PerlinNoise(seed * 31 + 7);
        _rivers = new RiverNetwork(seed, SeaLevel,
            (x, z) => ShapeHeight(_climate.Sample(x, z), x, z),
            (x, z) => _climate.Continentalness(x, z));

        // Lakes: big and deep, far apart. Ponds: small and shallow, more common.
        _lakes = new LakeField(seed, 0x1A4E, cellSize: 320, minRadius: 14, maxRadius: 32, chance: 0.4f, minDepth: 4, maxDepth: 8,
            SeaLevel, (x, z) => ShapeHeight(_climate.Sample(x, z), x, z), (x, z) => _climate.Continentalness(x, z));
        _ponds = new LakeField(seed, 0x90ED, cellSize: 112, minRadius: 4, maxRadius: 9, chance: 0.4f, minDepth: 2, maxDepth: 3.5f,
            SeaLevel, (x, z) => ShapeHeight(_climate.Sample(x, z), x, z), (x, z) => _climate.Continentalness(x, z));
    }

    // ---- Queries ----

    /// <summary>Height of the top solid block at a world column (before trees).</summary>
    public int GetHeight(int worldX, int worldZ) => SampleColumn(worldX, worldZ).Height;

    /// <summary>The biome the climate gives a column, ignoring rivers (cheap; used for searching).</summary>
    public Biome GetBiome(int worldX, int worldZ) => BiomeSource.Select(_climate.Sample(worldX, worldZ));

    /// <summary>The biome you'd actually stand in at a column, including rivers.</summary>
    public Biome GetBiomeAt(int worldX, int worldZ) => SampleColumn(worldX, worldZ).Biome;

    public Climate GetClimate(int worldX, int worldZ) => _climate.Sample(worldX, worldZ);

    /// <summary>Whether a column has water in it (sea, lake or river).</summary>
    public bool HasWater(int worldX, int worldZ) => SampleColumn(worldX, worldZ).WaterTop > 0;

    /// <summary>
    /// Finds a spot to start the player: dry land that isn't a beach, peak, riverbank or lake shore, preferably within a short
    /// walk of water (sea, river or lake) so a new world shows off the water straight away. If no coast is found within
    /// a couple of thousand blocks, the nearest ordinary dry land is used.
    /// </summary>
    public (int X, int Z) FindSpawn()
    {
        (int X, int Z)? anyLand = null;
        for (int radius = 0; radius < 2400; radius += 24)
            foreach (var (x, z) in Ring(0, 0, radius, 24))
            {
                var col = SampleColumn(x, z);
                if (col.Biome is Biome.Ocean or Biome.River or Biome.Lake or Biome.Beach or Biome.StonyPeaks or Biome.SnowyPeaks or Biome.Mountains) continue;
                if (col.WaterTop != 0 || col.Height <= SeaLevel + 2 || col.Height >= 80) continue;

                anyLand ??= (x, z);
                if (WaterNearby(x, z, 90)) return (x, z);
            }
        return anyLand ?? (0, 0);
    }

    /// <summary>True if there is water within roughly this many blocks of a spot (sampled in a ring and a half-ring).</summary>
    private bool WaterNearby(int x, int z, int distance)
    {
        for (int i = 0; i < 12; i++)
        {
            float angle = i / 12f * MathF.PI * 2f;
            float dx = MathF.Cos(angle), dz = MathF.Sin(angle);
            if (SampleColumn(x + (int)(dx * distance), z + (int)(dz * distance)).WaterTop > 0) return true;
            if (SampleColumn(x + (int)(dx * distance * 0.5f), z + (int)(dz * distance * 0.5f)).WaterTop > 0) return true;
        }
        return false;
    }

    /// <summary>Searches outward in rings for the nearest column of a biome. Handy for testing and for teleporting.</summary>
    public (int X, int Z)? FindBiome(Biome target, int fromX, int fromZ, int maxRadius = 8192)
    {
        // Rivers aren't part of the climate, so searching for one has to look at actual columns.
        Func<int, int, Biome> biomeAt = target is Biome.River or Biome.Lake ? GetBiomeAt : GetBiome;

        // Prefer a spot well inside the biome (all four points 24 blocks out agree) over its very edge.
        (int X, int Z)? edge = null;
        for (int radius = 0; radius <= maxRadius; radius += 16)
            foreach (var (x, z) in Ring(fromX, fromZ, radius, 16))
            {
                if (biomeAt(x, z) != target) continue;
                if (target is Biome.River or Biome.Lake) return (x, z);
                if (biomeAt(x - 24, z) == target && biomeAt(x + 24, z) == target &&
                    biomeAt(x, z - 24) == target && biomeAt(x, z + 24) == target) return (x, z);
                edge ??= (x, z);
            }
        return edge;
    }

    /// <summary>Points on the square ring at a given distance from the centre.</summary>
    private static IEnumerable<(int X, int Z)> Ring(int cx, int cz, int radius, int step)
    {
        if (radius == 0) { yield return (cx, cz); yield break; }
        for (int i = -radius; i <= radius; i += step)
        {
            yield return (cx + i, cz - radius);
            yield return (cx + i, cz + radius);
            if (i > -radius && i < radius)
            {
                yield return (cx - radius, cz + i);
                yield return (cx + radius, cz + i);
            }
        }
    }

    // ---- Terrain shaping ----

    private Column SampleColumn(int x, int z)
    {
        var climate = _climate.Sample(x, z);
        float terrain = ShapeHeight(climate, x, z);
        var column = new Column { Climate = climate, Biome = BiomeSource.Select(climate) };

        float height = terrain;
        var river = terrain >= SeaLevel ? _rivers.Query(x, z) : null;   // rivers have already ended by the time they meet the sea
        if (river is { } r)
        {
            column.River = r;
            float d = r.Distance, w = r.Width, water = r.WaterLevel;
            if (d <= w)
            {
                // The channel: a smooth trough, deepest in the middle, bottoming out at the water line at the edges.
                float t = d / w;
                height = water - RiverDepth * (1f - t * t);
                column.Biome = Biome.River;
            }
            else
            {
                // The banks: from just above the water line at the edge, blending back into the natural land.
                float t = Math.Clamp((d - w) / RiverNetwork.Bank, 0f, 1f);
                t = t * t * (3f - 2f * t);
                height = MathHelper.Lerp(water + 1f, terrain, t);
            }
        }

        // Lakes and ponds: a bowl in the middle, banks around it. They take over from a river where they overlap.
        var lake = terrain >= SeaLevel ? NearestLake(x, z) : null;
        if (lake is { } l)
        {
            column.Lake = l;
            if (l.InBasin)
            {
                // A smooth bowl, deepest in the middle and meeting the water line at the shore.
                float t = Math.Clamp(l.CentreDistance / l.Radius, 0f, 1f);
                height = l.WaterLevel - l.Depth * (1f - t * t);
                column.Biome = Biome.Lake;
            }
            else
            {
                float t = Math.Clamp(l.EdgeDistance / LakeField.Bank, 0f, 1f);
                t = t * t * (3f - 2f * t);
                height = MathHelper.Lerp(l.WaterLevel + 1f, terrain, t);
            }
        }

        column.Height = Math.Clamp((int)MathF.Floor(height), 2, Chunk.Height - 6);
        if (column.Height < SeaLevel) column.WaterTop = SeaLevel;
        if (river is { InChannel: true } channel && !(lake is { InBasin: true }))
        {
            int riverTop = (int)MathF.Floor(channel.WaterLevel);
            if (riverTop > column.Height) column.WaterTop = Math.Max(column.WaterTop, riverTop);
        }
        if (lake is { InBasin: true } basin)
        {
            int lakeTop = (int)MathF.Floor(basin.WaterLevel);
            if (lakeTop > column.Height) column.WaterTop = Math.Max(column.WaterTop, lakeTop);
        }
        return column;
    }

    /// <summary>The closer of the nearest lake and nearest pond, by distance from shore.</summary>
    private LakeSample? NearestLake(int x, int z)
    {
        var lake = _lakes.Query(x, z);
        var pond = _ponds.Query(x, z);
        if (lake is null) return pond;
        if (pond is null) return lake;
        return lake.Value.EdgeDistance <= pond.Value.EdgeDistance ? lake : pond;
    }

    /// <summary>Land height before rivers: continentalness gives the base, erosion the ruggedness, ridges the peaks.</summary>
    private float ShapeHeight(Climate c, int x, int z)
    {
        float baseHeight = BaseHeight.Evaluate(c.Continentalness);
        float relief = Relief.Evaluate(c.Erosion);

        // Mountains only rise well inland, never right at the coast.
        float inland = SmoothStep((c.Continentalness + 0.1f) / 0.45f);

        // Ridges go up a lot, valleys dip a little.
        float pv = c.PeaksAndValleys;
        float ridges = (pv >= 0 ? pv * 50f : pv * 14f) * relief * inland;

        // Small rolling bumps, bigger where the land is rugged.
        float hills = _detail.Fractal(x * 0.012f, z * 0.012f, 4) * (3f + 12f * relief);

        float h = baseHeight + ridges + hills;

        // Inland ground never dips to the waterline; only rivers and the coast get that low.
        if (c.Continentalness > -0.1f) h = MathF.Max(h, SeaLevel + 2);

        return Math.Clamp(h, 2f, Chunk.Height - 6f);
    }

    // ---- Chunk generation ----

    public void Generate(Chunk chunk)
    {
        int originX = chunk.WorldX - Pad, originZ = chunk.WorldZ - Pad;

        var columns = new Column[CacheSize * CacheSize];
        for (int j = 0; j < CacheSize; j++)
            for (int i = 0; i < CacheSize; i++)
                columns[j * CacheSize + i] = SampleColumn(originX + i, originZ + j);

        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            int index = (z + Pad) * CacheSize + x + Pad;
            var col = columns[index];
            FillColumn(chunk, x, z, col, SurfaceAt(columns, index, chunk.WorldX + x, chunk.WorldZ + z));
            chunk.SetFlow(x, z, col.River is { InChannel: true } r ? r.Flow : Vector2.Zero);
        }

        PlaceTrees(chunk, columns);
    }

    /// <summary>Records which way each river column flows, so the water can be drawn moving. Not saved, so also called when loading.</summary>
    public void FillRiverFlow(Chunk chunk)
    {
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            var river = SampleColumn(chunk.WorldX + x, chunk.WorldZ + z).River;
            chunk.SetFlow(x, z, river is { InChannel: true } r ? r.Flow : Vector2.Zero);
        }
    }

    private static void FillColumn(Chunk chunk, int x, int z, Column col, Surface s)
    {
        int height = col.Height;
        for (int y = 0; y <= height; y++)
        {
            BlockType type;
            int depth = height - y;   // 0 = the top block
            if (y == 0)                                   type = BlockType.Bedrock;
            else if (depth == 0)                          type = s.Top;
            else if (depth <= s.FillerDepth)              type = s.Filler;
            else if (depth <= s.FillerDepth + s.DeepDepth) type = s.Deep;
            else                                          type = BlockType.Stone;
            chunk.Set(x, y, z, type);
        }

        // Water: all source blocks (level 0), up to the sea or river surface.
        for (int y = height + 1; y <= col.WaterTop; y++)
            chunk.Set(x, y, z, BlockType.Water);
    }

    /// <summary>Decides what the ground is made of at a column, from its biome, height and steepness.</summary>
    private Surface SurfaceAt(Column[] columns, int index, int worldX, int worldZ)
    {
        var col = columns[index];
        var def = BiomeRegistry.Get(col.Biome);
        var surface = new Surface(def.Top, def.Filler, def.FillerDepth, def.Deep, def.DeepDepth);

        int h = col.Height;
        int slope = Math.Max(
            Math.Max(Math.Abs(h - columns[index - 1].Height), Math.Abs(h - columns[index + 1].Height)),
            Math.Max(Math.Abs(h - columns[index - CacheSize].Height), Math.Abs(h - columns[index + CacheSize].Height)));
        float patch = _patches.Noise(worldX * 0.07f, worldZ * 0.07f);   // -1..1 blotches for gravel, podzol...

        switch (col.Biome)
        {
            case Biome.Ocean:
                if (patch > 0.25f) surface = surface with { Top = BlockType.Gravel };
                break;

            case Biome.River:
            case Biome.Lake:
                // A river or lake bed of gravel and sand.
                surface = new Surface(patch > 0.1f ? BlockType.Gravel : BlockType.Sand, BlockType.Sand, 3, BlockType.Stone, 0);
                break;

            case Biome.Taiga:
                if (patch > 0.1f) surface = surface with { Top = BlockType.Podzol };
                break;

            case Biome.Mountains:
                if (slope >= SteepSlope)      surface = new Surface(patch > 0.35f ? BlockType.Gravel : BlockType.Stone, BlockType.Stone, 3, BlockType.Stone, 0);
                else if (h >= SnowLine)       surface = new Surface(BlockType.Snow, BlockType.Snow, 1, BlockType.Stone, 0);
                break;

            case Biome.StonyPeaks:
                if (patch > 0.3f) surface = surface with { Top = BlockType.Gravel };
                break;

            case Biome.SnowyPeaks:
                if (slope >= SteepSlope + 1) surface = new Surface(BlockType.Stone, BlockType.Stone, 3, BlockType.Stone, 0);
                break;
        }

        // Low ground next to the sea, and the banks right beside a river, are sandy whatever the biome (desert already is).
        bool sandy = h <= SeaLevel + 1 && col.Biome is not (Biome.Ocean or Biome.Beach or Biome.Desert or Biome.River or Biome.Lake);
        if (col.River is { } river && col.Biome != Biome.River && river.Distance < river.Width + 2.5f && h <= river.WaterLevel + 2)
            sandy = true;
        if (col.Lake is { } shore && col.Biome != Biome.Lake && shore.EdgeDistance < 2.5f && h <= shore.WaterLevel + 2)
            sandy = true;
        if (sandy) surface = new Surface(BlockType.Sand, BlockType.Sand, 3, BlockType.Stone, 0);

        return surface;
    }

    // ---- Trees ----

    private void PlaceTrees(Chunk chunk, Column[] columns)
    {
        int x0 = chunk.WorldX, z0 = chunk.WorldZ;
        int originX = x0 - Pad, originZ = z0 - Pad;
        int reach = TreeBuilder.MaxReach;

        // Visit every tree cell whose tree could reach this chunk, in a fixed global order. Neighbouring chunks
        // visit the shared trees in the same order, so overlapping trees resolve identically on both sides.
        for (int cz = FloorDiv(z0 - reach, TreeCell); cz <= FloorDiv(z0 + Chunk.Size - 1 + reach, TreeCell); cz++)
        for (int cx = FloorDiv(x0 - reach, TreeCell); cx <= FloorDiv(x0 + Chunk.Size - 1 + reach, TreeCell); cx++)
        {
            uint h = TreeBuilder.Hash(_seed, cx, 0x7EE, cz);
            int tx = cx * TreeCell + (int)(h & 3);
            int tz = cz * TreeCell + (int)((h >> 2) & 3);
            if (tx < x0 - reach || tx >= x0 + Chunk.Size + reach || tz < z0 - reach || tz >= z0 + Chunk.Size + reach) continue;

            int index = (tz - originZ) * CacheSize + (tx - originX);
            var def = BiomeRegistry.Get(columns[index].Biome);
            if (def.Trees == null || def.TreeChance <= 0f) continue;
            if (((h >> 8) & 0xFFFF) / 65536f >= def.TreeChance) continue;
            if (columns[index].WaterTop > 0) continue;   // nothing grows under water

            var kind = PickKind(def, h >> 24);
            var ground = SurfaceAt(columns, index, tx, tz).Top;
            if (!CanGrowOn(kind, ground)) continue;

            int baseY = columns[index].Height + 1;
            uint variation = TreeBuilder.Hash(_seed, tx, baseY, tz);
            TreeBuilder.Build(kind, tx, baseY, tz, variation, (x, y, z, block) => PlaceInChunk(chunk, x, y, z, block));
        }
    }

    private static TreeKind PickKind((TreeKind Kind, int Weight)[] kinds, uint roll)
    {
        int total = 0;
        foreach (var k in kinds) total += k.Weight;
        int pick = (int)(roll % (uint)total);
        foreach (var k in kinds)
        {
            if (pick < k.Weight) return k.Kind;
            pick -= k.Weight;
        }
        return kinds[0].Kind;
    }

    private static TreeKind PickKind(BiomeDef def, uint roll) => PickKind(def.Trees, roll);

    private static bool CanGrowOn(TreeKind kind, BlockType ground) => kind == TreeKind.Cactus
        ? ground == BlockType.Sand
        : ground is BlockType.Grass or BlockType.Dirt or BlockType.SnowyGrass or BlockType.Podzol or BlockType.DryGrass or BlockType.PaleMoss;

    /// <summary>
    /// Writes a tree block if it falls inside this chunk. Leaves only fill empty space; logs also replace leaves,
    /// so the result doesn't depend on which tree was placed first.
    /// </summary>
    private static void PlaceInChunk(Chunk chunk, int x, int y, int z, BlockType block)
    {
        int lx = x - chunk.WorldX, lz = z - chunk.WorldZ;
        if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size || (uint)y >= Chunk.Height) return;

        var current = chunk.Get(lx, y, lz);
        bool replace = current == BlockType.Air || (!TreeBuilder.IsLeaves(block) && TreeBuilder.IsLeaves(current));
        if (replace) chunk.Set(lx, y, lz, block);
    }

    // ---- Helpers ----

    private static int FloorDiv(int a, int b) => (int)MathF.Floor(a / (float)b);

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Piecewise-linear curve through (x, y) control points, flat beyond the ends.</summary>
    private sealed class Curve
    {
        private readonly (float X, float Y)[] _points;
        public Curve(params (float X, float Y)[] points) => _points = points;

        public float Evaluate(float x)
        {
            if (x <= _points[0].X) return _points[0].Y;
            for (int i = 1; i < _points.Length; i++)
            {
                if (x > _points[i].X) continue;
                var (x0, y0) = _points[i - 1];
                var (x1, y1) = _points[i];
                return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
            }
            return _points[^1].Y;
        }
    }
}
