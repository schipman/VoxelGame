using Microsoft.Xna.Framework;

namespace VoxelGame.Terrain;

/// <summary>What the nearest river looks like from one spot.</summary>
public readonly record struct RiverSample(float Distance, float Width, float WaterLevel, Vector2 Flow)
{
    /// <summary>True if the spot is inside the river channel (under its water).</summary>
    public bool InChannel => Distance <= Width;
}

/// <summary>
/// Rivers that start on high ground and run downhill to the sea, forking on the way.
///
/// The world generates in independent chunks, so rivers can't be grown by looking at neighbouring chunks.
/// Instead the world is cut into large regions, and each region deterministically spawns a few river sources
/// on its highest inland ground. Each source is traced downhill as a chain of short straight segments, steered by:
///   - the slope of the land (water runs downhill),
///   - the slope of continentalness (so flat land still drains toward the coast),
///   - inertia (rivers don't turn on a dime),
///   - smooth fractal noise, which makes them meander.
/// At random points a river forks: a second river starts there, heading off at an angle, and finds its own way down.
/// Every segment is filed into a spatial grid, so a chunk only has to look at the segments near it.
/// The result depends only on the seed and the terrain, so any chunk can be generated at any time.
/// </summary>
public sealed class RiverNetwork
{
    private const int RegionSize = 1536;      // blocks; each region spawns its own sources
    private const int RegionReach = 2;        // regions to either side whose rivers can reach a point (longest river / region size)
    private const int GridCell = 64;          // spatial index cell size

    private const float StepLength = 14f;     // blocks per segment
    private const int MaxSteps = 210;         // longest river is ~2900 blocks
    private const int HighlandSources = 3;     // from the very highest ground in the region (usually far inland)
    private const int FoothillSources = 3;     // from raised ground close to the coast, so plenty of rivers reach the sea
    private const int SourceCandidates = 80;
    private const float MinSourceHeight = 64f;
    private const float MinFoothillHeight = 56f;
    private const float FoothillMaxContinentalness = 0.3f;

    private const float MaxWidth = 9f;
    private const float WidthGrowth = 0.05f;  // per step downstream
    private const int MaxForkDepth = 2;       // a fork can fork, but its forks can't
    private const int MaxForksPerRiver = 2;
    private const int MinStepsBeforeFork = 20;
    private const float ForkChance = 0.04f;   // per step, once past MinStepsBeforeFork
    private const float ForkMaxContinentalness = 0.4f;   // only fork in the lower reaches, near the coast, like a delta
    private const int MinStepsLeftToFork = 60;           // ...and only if the fork has room to get to the sea

    /// <summary>How far past the water's edge the river reshapes the land (the banks).</summary>
    public const float Bank = 6f;

    // A segment can affect columns up to this far from its centre line; used to file it into grid cells.
    private const float Influence = MaxWidth * 2.5f + Bank + 2f;

    private readonly int _seed;
    private readonly int _seaLevel;
    private readonly Func<int, int, float> _terrainHeight;      // land height WITHOUT rivers
    private readonly Func<int, int, float> _continentalness;
    private readonly PerlinNoise _meander;

    /// <summary>Counters for tuning and tests: how many rivers were traced and how they ended.</summary>
    public int SourceCount { get; private set; }
    public int TraceCount { get; private set; }
    public int ReachedSeaCount { get; private set; }
    public int GaveUpCount { get; private set; }     // stuck in a basin
    public int PondCount { get; private set; }       // ran out of length and pooled
    public int SegmentCount { get; private set; }
    public int[] TracesByDepth { get; } = new int[MaxForkDepth + 1];
    public int[] SeaByDepth { get; } = new int[MaxForkDepth + 1];

    // Regions are traced the first time anything needs them, so these fill in as the world is explored. Chunks are generated
    // on several threads at once, so everything that reads or builds them happens under this lock.
    private readonly object _sync = new();

    private readonly HashSet<(int, int)> _traced = new();
    private readonly HashSet<(int, int)> _ready = new();
    private readonly Dictionary<(int, int), List<Segment>> _grid = new();

    private readonly struct Segment
    {
        public readonly float X0, Z0, X1, Z1;
        public readonly float Water0, Water1, Width0, Width1;

        public Segment(float x0, float z0, float x1, float z1, float water0, float water1, float width0, float width1)
        {
            X0 = x0; Z0 = z0; X1 = x1; Z1 = z1;
            Water0 = water0; Water1 = water1; Width0 = width0; Width1 = width1;
        }
    }

    public RiverNetwork(int seed, int seaLevel, Func<int, int, float> terrainHeight, Func<int, int, float> continentalness)
    {
        _seed = seed;
        _seaLevel = seaLevel;
        _terrainHeight = terrainHeight;
        _continentalness = continentalness;
        _meander = new PerlinNoise(seed * 31 + 8);
    }

    // ---- Queries ----

    /// <summary>
    /// The river closest (in terms of its own width) to a world column, or null if none is near enough to matter.
    /// Safe to call from anywhere; it traces whatever regions it needs on first use.
    /// </summary>
    public RiverSample? Query(int x, int z)
    {
        lock (_sync) return QueryLocked(x, z);
    }

    private RiverSample? QueryLocked(int x, int z)
    {
        EnsureAround(x, z);
        if (!_grid.TryGetValue((FloorDiv(x, GridCell), FloorDiv(z, GridCell)), out var segments)) return null;

        float bestScore = Bank;   // only rivers whose edge is within the bank distance count
        RiverSample? best = null;
        foreach (var s in segments)
        {
            float dx = s.X1 - s.X0, dz = s.Z1 - s.Z0;
            float len2 = dx * dx + dz * dz;
            float t = len2 < 1e-6f ? 0f : Math.Clamp(((x - s.X0) * dx + (z - s.Z0) * dz) / len2, 0f, 1f);
            float px = s.X0 + dx * t - x, pz = s.Z0 + dz * t - z;
            float distance = MathF.Sqrt(px * px + pz * pz);
            float width = s.Width0 + (s.Width1 - s.Width0) * t;

            float score = distance - width;
            if (score >= bestScore) continue;
            bestScore = score;

            float length = MathF.Sqrt(len2);
            best = new RiverSample(distance, width, s.Water0 + (s.Water1 - s.Water0) * t,
                length < 1e-6f ? Vector2.Zero : new Vector2(dx / length, dz / length));
        }
        return best;
    }

    // ---- Building the network ----

    /// <summary>Makes sure every region whose rivers could touch this point has been traced.</summary>
    private void EnsureAround(int x, int z)
    {
        int rx = FloorDiv(x, RegionSize), rz = FloorDiv(z, RegionSize);
        if (_ready.Contains((rx, rz))) return;

        for (int dx = -RegionReach; dx <= RegionReach; dx++)
            for (int dz = -RegionReach; dz <= RegionReach; dz++)
                if (_traced.Add((rx + dx, rz + dz)))
                    TraceRegion(rx + dx, rz + dz);
        _ready.Add((rx, rz));
    }

    private void TraceRegion(int regionX, int regionZ)
    {
        // Scatter candidate points over the region, then pick sources: the highest ground, plus some raised ground
        // near the coast (those rivers are short enough to reach the sea).
        var highland = new List<(float Height, int X, int Z)>();
        var foothills = new List<(float Height, int X, int Z)>();
        for (int i = 0; i < SourceCandidates; i++)
        {
            uint h = TreeBuilder.Hash(_seed ^ 0x51ED, regionX, i, regionZ);
            int x = regionX * RegionSize + (int)(h % RegionSize);
            int z = regionZ * RegionSize + (int)((h >> 12) * 2654435761u % RegionSize);
            float height = _terrainHeight(x, z);
            float continentalness = _continentalness(x, z);
            if (continentalness <= 0.05f) continue;
            if (height >= MinSourceHeight && continentalness > 0.1f) highland.Add((height, x, z));
            if (height >= MinFoothillHeight && continentalness <= FoothillMaxContinentalness) foothills.Add((height, x, z));
        }
        highland.Sort((a, b) => b.Height.CompareTo(a.Height));
        foothills.Sort((a, b) => b.Height.CompareTo(a.Height));

        var sources = new List<(float Height, int X, int Z)>();
        void Pick(List<(float Height, int X, int Z)> candidates, int count)
        {
            int taken = 0;
            foreach (var c in candidates)
            {
                if (taken >= count) break;
                // Keep sources apart, otherwise the best hill ends up with all of them.
                if (sources.Any(s => (s.X - c.X) * (s.X - c.X) + (s.Z - c.Z) * (s.Z - c.Z) < 300 * 300)) continue;
                sources.Add(c);
                taken++;
            }
        }
        Pick(highland, HighlandSources);
        Pick(foothills, FoothillSources);

        int n = 0;
        foreach (var s in sources)
        {
            SourceCount++;
            var rng = new Random((int)TreeBuilder.Hash(_seed, regionX, n++, regionZ));
            float angle = (float)(rng.NextDouble() * MathHelper.TwoPi);
            Trace(s.X, s.Z, new Vector2(MathF.Cos(angle), MathF.Sin(angle)), 2.2f, s.Height - 1f, 0, MaxSteps, rng);
        }
    }

    /// <summary>Follows the land downhill from a point, laying down segments, until it reaches the sea or runs out of road.</summary>
    /// <param name="stepsLeft">
    /// How many more segments this river may lay. A fork only gets what its parent had left, so no river system
    /// ever spreads further than MaxSteps from its source: that bound is what lets a point know exactly which
    /// regions' rivers could reach it (RegionReach), and keeps the result independent of the order things are traced in.
    /// </param>
    private void Trace(float x, float z, Vector2 dir, float startWidth, float startWater, int depth, int stepsLeft, Random rng)
    {
        TraceCount++;
        TracesByDepth[depth]++;
        float water = startWater;
        int uphillSteps = 0, forks = 0;
        float lastHeight = _terrainHeight((int)x, (int)z);

        for (int step = 0; step < stepsLeft; step++)
        {
            int ix = (int)MathF.Round(x), iz = (int)MathF.Round(z);
            float width = Math.Min(MaxWidth, startWidth + WidthGrowth * step);

            // Steer: downhill (measured over a wide baseline, so little bumps don't trap the river)
            // plus downward along continentalness, so flat land still drains to the coast.
            var downhill = new Vector2(
                _terrainHeight(ix - 48, iz) - _terrainHeight(ix + 48, iz),
                _terrainHeight(ix, iz - 48) - _terrainHeight(ix, iz + 48));
            var toSea = new Vector2(
                _continentalness(ix - 130, iz) - _continentalness(ix + 130, iz),
                _continentalness(ix, iz - 130) - _continentalness(ix, iz + 130));

            // The wide-scale direction of the coast is the main pull; the local slope adds the wiggle.
            var desired = Vector2.Zero;
            if (toSea.LengthSquared() > 1e-8f) desired += Vector2.Normalize(toSea);
            if (downhill.LengthSquared() > 1e-4f) desired += 0.5f * Vector2.Normalize(downhill);

            if (desired.LengthSquared() > 1e-6f) dir = Vector2.Normalize(dir * 0.6f + Vector2.Normalize(desired) * 0.4f);

            // Smooth fractal noise nudges the heading left and right: that's the meander. It has to be gentle,
            // since the nudge is applied every step and a steady push would just curl the river into a circle.
            float bend = _meander.Fractal(x / 120f, z / 120f, 2) * 0.09f;
            dir = Rotate(dir, bend);

            float nx = x + dir.X * StepLength, nz = z + dir.Y * StepLength;
            float height = _terrainHeight((int)MathF.Round(nx), (int)MathF.Round(nz));
            bool reachedSea = height <= _seaLevel + 1.5f || _continentalness((int)nx, (int)nz) < -0.1f;

            // The surface can only fall as the river goes on, and sits just under the surrounding ground.
            float nextWater = reachedSea ? _seaLevel : Math.Max(Math.Min(water, height - 1f), _seaLevel);
            float nextWidth = Math.Min(MaxWidth, startWidth + WidthGrowth * (step + 1)) * (reachedSea ? 1.5f : 1f);
            AddSegment(new Segment(x, z, nx, nz, water, nextWater, width, nextWidth));

            if (reachedSea) { ReachedSeaCount++; SeaByDepth[depth]++; return; }

            // Stuck in a basin? Give up rather than wander uphill forever.
            uphillSteps = height > lastHeight + 0.3f ? uphillSteps + 1 : 0;
            if (uphillSteps > 14) { GaveUpCount++; return; }
            lastHeight = height;

            // Maybe split in two. The new branch heads off at an angle and finds its own way down.
            if (depth < MaxForkDepth && forks < MaxForksPerRiver && step >= MinStepsBeforeFork && stepsLeft - step > MinStepsLeftToFork
                && _continentalness((int)nx, (int)nz) < ForkMaxContinentalness && rng.NextDouble() < ForkChance)
            {
                forks++;
                float side = rng.Next(2) == 0 ? -1f : 1f;
                float spread = 0.45f + (float)rng.NextDouble() * 0.5f;
                Trace(nx, nz, Rotate(dir, side * spread), nextWidth * 0.8f, nextWater, depth + 1, stepsLeft - step - 1, new Random(rng.Next()));
            }

            x = nx; z = nz; water = nextWater;
        }

        // Out of road without reaching the sea: the river pools into a pond.
        PondCount++;
        float endWidth = Math.Min(MaxWidth, startWidth + WidthGrowth * stepsLeft);
        AddSegment(new Segment(x, z, x + dir.X * 10f, z + dir.Y * 10f, water, water, endWidth, endWidth * 2.2f));
        AddSegment(new Segment(x + dir.X * 10f, z + dir.Y * 10f, x + dir.X * 20f, z + dir.Y * 20f, water, water, endWidth * 2.2f, endWidth * 1.2f));
    }

    private void AddSegment(Segment s)
    {
        SegmentCount++;
        float minX = Math.Min(s.X0, s.X1) - Influence, maxX = Math.Max(s.X0, s.X1) + Influence;
        float minZ = Math.Min(s.Z0, s.Z1) - Influence, maxZ = Math.Max(s.Z0, s.Z1) + Influence;

        for (int gx = FloorDiv((int)minX, GridCell); gx <= FloorDiv((int)maxX, GridCell); gx++)
        for (int gz = FloorDiv((int)minZ, GridCell); gz <= FloorDiv((int)maxZ, GridCell); gz++)
        {
            if (!_grid.TryGetValue((gx, gz), out var list)) _grid[(gx, gz)] = list = new List<Segment>();
            list.Add(s);
        }
    }

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static int FloorDiv(int a, int b) => (int)MathF.Floor(a / (float)b);
}
