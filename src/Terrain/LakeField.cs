namespace VoxelGame.Terrain;

/// <summary>What the nearest lake or pond looks like from one spot.</summary>
/// <param name="EdgeDistance">Distance from the shoreline: negative inside the lake, positive outside.</param>
/// <param name="Radius">Radius of the lake at this angle (the outline is irregular).</param>
/// <param name="CentreDistance">Distance from the middle of the lake.</param>
public readonly record struct LakeSample(float EdgeDistance, float Radius, float CentreDistance, float WaterLevel, float Depth)
{
    public bool InBasin => EdgeDistance <= 0f;
}

/// <summary>
/// Scatters lakes (or ponds: the same thing, smaller) over inland ground.
///
/// The world is cut into a grid of cells. Each cell deterministically decides, from the seed alone, whether it holds a
/// lake and where. A lake is only placed on fairly flat, inland, above-sea-level ground, so it never ends up
/// on a mountainside or at the coast. Its water level is set just under the lowest point of the rim, so the water
/// can never spill over: the rim is always at least a block above it.
///
/// A lake is a rough circle whose edge wobbles with noise (so shorelines look natural) and whose bed is a smooth bowl,
/// deepest in the middle. The generator turns the resulting samples into terrain and water.
/// </summary>
public sealed class LakeField
{
    /// <summary>How far past the shoreline a lake reshapes the land (its banks).</summary>
    public const float Bank = 6f;

    private const float OutlineWobble = 0.32f;   // how much the shoreline strays from a circle (fraction of the radius)
    private const float OutlineScale = 14f;      // size of the wobbles, in blocks

    private readonly int _seed;
    private readonly int _cellSize;
    private readonly float _minRadius, _maxRadius, _chance, _minDepth, _maxDepth;
    private readonly int _seaLevel;
    private readonly Func<int, int, float> _terrainHeight;
    private readonly Func<int, int, float> _continentalness;
    private readonly PerlinNoise _outline;
    private readonly Dictionary<(int, int), Lake?> _cells = new();
    private readonly object _sync = new();   // cells are decided on first use, from any generator thread

    /// <summary>For tuning and tests: how many grid cells have been decided, and how many of them got a lake.</summary>
    public int CellsDecided { get; private set; }
    public int LakesPlaced { get; private set; }

    private readonly record struct Lake(float X, float Z, float Radius, float WaterLevel, float Depth);

    /// <param name="cellSize">Grid size in blocks; must exceed the biggest lake's reach (radius * 1.4 + bank).</param>
    /// <param name="chance">Chance that a cell holds a lake (before the ground-suitability check).</param>
    public LakeField(int seed, int salt, int cellSize, float minRadius, float maxRadius, float chance,
        float minDepth, float maxDepth, int seaLevel,
        Func<int, int, float> terrainHeight, Func<int, int, float> continentalness)
    {
        _seed = seed ^ salt;
        _cellSize = cellSize;
        _minRadius = minRadius; _maxRadius = maxRadius; _chance = chance;
        _minDepth = minDepth; _maxDepth = maxDepth;
        _seaLevel = seaLevel;
        _terrainHeight = terrainHeight;
        _continentalness = continentalness;
        _outline = new PerlinNoise(_seed * 31 + 9);
    }

    /// <summary>The lake whose shore is nearest to a point (counting only ones near enough to matter), or null.</summary>
    public LakeSample? Query(int x, int z)
    {
        lock (_sync) return QueryLocked(x, z);
    }

    private LakeSample? QueryLocked(int x, int z)
    {
        int cx = FloorDiv(x, _cellSize), cz = FloorDiv(z, _cellSize);
        LakeSample? best = null;

        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (GetLake(cx + dx, cz + dz) is not { } lake) continue;

            float ox = x - lake.X, oz = z - lake.Z;
            float distance = MathF.Sqrt(ox * ox + oz * oz);
            if (distance > lake.Radius * (1f + OutlineWobble) + Bank) continue;   // far outside even the widest outline

            // The shoreline wanders around the nominal radius.
            float radius = lake.Radius * (1f + OutlineWobble * _outline.Fractal(x / OutlineScale, z / OutlineScale, 2));
            float edge = distance - radius;
            if (edge > Bank) continue;
            if (best is { } current && current.EdgeDistance <= edge) continue;

            best = new LakeSample(edge, radius, distance, lake.WaterLevel, lake.Depth);
        }
        return best;
    }

    private Lake? GetLake(int cellX, int cellZ)
    {
        if (_cells.TryGetValue((cellX, cellZ), out var cached)) return cached;
        var lake = Decide(cellX, cellZ);
        CellsDecided++;
        if (lake != null) LakesPlaced++;
        return _cells[(cellX, cellZ)] = lake;
    }

    /// <summary>Decides, from the seed alone, whether this cell has a lake, and if so where and how big.</summary>
    private Lake? Decide(int cellX, int cellZ)
    {
        uint h = TreeBuilder.Hash(_seed, cellX, 0x1A4E, cellZ);
        if ((h & 0xFFFF) / 65536f >= _chance) return null;

        uint h2 = TreeBuilder.Hash(_seed, cellX, 0x2B5F, cellZ);
        float radius = _minRadius + ((h >> 16) & 0xFF) / 255f * (_maxRadius - _minRadius);
        float depth = _minDepth + (h2 & 0xFF) / 255f * (_maxDepth - _minDepth);
        float x = cellX * _cellSize + ((h2 >> 8) & 0xFFFF) / 65536f * _cellSize;
        float z = cellZ * _cellSize + ((h2 >> 24) & 0xFF) / 255f * _cellSize;

        // Only on inland ground that isn't a mountain: well above the sea, and inland enough not to be a coast.
        int ix = (int)x, iz = (int)z;
        float centre = _terrainHeight(ix, iz);
        if (centre < _seaLevel + 4 || centre > 90f || _continentalness(ix, iz) < 0.05f) return null;

        // The rim: how high the land is all the way round. The water must stay below its lowest point.
        float lowest = centre, highest = centre;
        for (int i = 0; i < 12; i++)
        {
            float angle = i / 12f * MathF.PI * 2f;
            float rx = x + MathF.Cos(angle) * radius * 1.3f, rz = z + MathF.Sin(angle) * radius * 1.3f;
            float height = _terrainHeight((int)rx, (int)rz);
            if (_continentalness((int)rx, (int)rz) < -0.05f) return null;    // too close to the sea
            lowest = Math.Min(lowest, height);
            highest = Math.Max(highest, height);
        }
        if (highest - lowest > 3f + radius * 0.5f) return null;   // too steep: it would be a mountain tarn

        float water = MathF.Floor(lowest) - 1f;
        if (water < _seaLevel + 1) return null;
        return new Lake(x, z, radius, water, depth);
    }

    private static int FloorDiv(int a, int b) => (int)MathF.Floor(a / (float)b);
}
