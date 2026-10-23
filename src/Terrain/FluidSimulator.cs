using VoxelGame.Blocks;

namespace VoxelGame.Terrain;

/// <summary>
/// Makes water flow, in the manner of Minecraft. Every water block has a level:
///   0       a source block: full, and never runs dry
///   1 - 7   flowing water: 1 is thickest (just fed from above), 7 is the thinnest, at the edge of the puddle
///
/// Each simulation step looks at one water block and does the first thing that applies:
///   1. Dry up: flowing water with nothing feeding it (no water above, no thicker neighbour) disappears.
///   2. Fall: if there is air below, water pours down into it (level 1, so a waterfall is a column of thick water).
///   3. Spread: if it is resting on something solid and isn't at level 7, it runs out one block sideways into air,
///      one level thinner.
/// Whenever a block changes, the blocks around it are queued for a step, so changes ripple outward a few
/// times per second. Generated oceans and rivers are all source blocks, so they stay put until something nearby changes.
/// </summary>
public sealed class FluidSimulator
{
    private const float TickSeconds = 0.2f;    // how often water advances
    private const int MaxPerTick = 800;        // cap, so a huge flood can't stall a frame
    public const int MaxLevel = 7;

    /// <summary>
    /// How tall water of a given level stands in its block, as a fraction of a block: a source block sits just
    /// under the brim, and flowing water gets thinner the further it has run. The mesher, the player and the
    /// water-surface queries all use this, so what you see is what you swim in.
    /// </summary>
    public static float SurfaceHeight(int level) => level == 0 ? 0.88f : Math.Max(0.12f, 0.88f - level * 0.1f);

    private static readonly (int X, int Z)[] Horizontal = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    private readonly World _world;
    private readonly Queue<(int X, int Y, int Z)> _pending = new();
    private readonly HashSet<(int X, int Y, int Z)> _queued = new();
    private float _timer;

    public FluidSimulator(World world)
    {
        _world = world;
    }

    /// <summary>Number of blocks waiting for a simulation step (0 once everything has settled).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Called whenever a block changes: water in or next to the change may need to move.</summary>
    public void OnBlockChanged(int x, int y, int z)
    {
        bool near = _world.IsWater(x, y, z) || _world.IsWater(x + 1, y, z) || _world.IsWater(x - 1, y, z)
                 || _world.IsWater(x, y + 1, z) || _world.IsWater(x, y - 1, z)
                 || _world.IsWater(x, y, z + 1) || _world.IsWater(x, y, z - 1);
        if (!near) return;

        Schedule(x, y, z);
        Schedule(x + 1, y, z); Schedule(x - 1, y, z);
        Schedule(x, y + 1, z); Schedule(x, y - 1, z);
        Schedule(x, y, z + 1); Schedule(x, y, z - 1);
    }

    private void Schedule(int x, int y, int z)
    {
        if (y < 0 || y >= Chunk.Height) return;
        if (_queued.Add((x, y, z))) _pending.Enqueue((x, y, z));
    }

    public void Update(float dt)
    {
        _timer += dt;
        if (_timer < TickSeconds) return;
        _timer = Math.Min(_timer - TickSeconds, TickSeconds);   // don't try to catch up after a long frame
        Step();
    }

    /// <summary>Runs one simulation tick: processes everything that was queued at the start of it.</summary>
    public void Step()
    {
        int count = Math.Min(_pending.Count, MaxPerTick);
        for (int i = 0; i < count; i++)
        {
            var (x, y, z) = _pending.Dequeue();
            _queued.Remove((x, y, z));
            Process(x, y, z);
        }
    }

    private void Process(int x, int y, int z)
    {
        if (!_world.IsWater(x, y, z)) return;

        int level = _world.GetFluidLevel(x, y, z);
        bool waterAbove = _world.IsWater(x, y + 1, z);

        // Water directly under other water is just a falling stream: always the thickest flowing level.
        if (waterAbove && level > 1)
        {
            _world.SetBlock(x, y, z, BlockType.Water, 1);
            return;   // that change queues this block again
        }

        // 1. Dry up.
        if (level > 0 && !IsFed(x, y, z, level, waterAbove))
        {
            _world.SetBlock(x, y, z, BlockType.Air);
            return;
        }

        // 2. Fall.
        var below = _world.GetBlock(x, y - 1, z);
        if (below == BlockType.Air)
        {
            _world.SetBlock(x, y - 1, z, BlockType.Water, 1);
            return;
        }
        if (BlockRegistry.IsLiquid(below) && _world.GetFluidLevel(x, y - 1, z) > 1)
        {
            _world.SetBlock(x, y - 1, z, BlockType.Water, 1);
            return;
        }

        // 3. Spread sideways, but only once it has landed on something solid. Water resting on more water is just
        //    part of a pool or a falling stream, and doesn't fan out.
        if (BlockRegistry.IsLiquid(below)) return;
        int effective = waterAbove ? 1 : level;
        if (effective >= MaxLevel) return;
        foreach (var (dx, dz) in Horizontal)
        {
            var side = _world.GetBlock(x + dx, y, z + dz);
            if (side == BlockType.Air)
                _world.SetBlock(x + dx, y, z + dz, BlockType.Water, effective + 1);
            else if (BlockRegistry.IsLiquid(side) && _world.GetFluidLevel(x + dx, y, z + dz) > effective + 1)
                _world.SetBlock(x + dx, y, z + dz, BlockType.Water, effective + 1);
        }
    }

    /// <summary>Flowing water survives if water above is feeding it, or a sideways neighbour is thicker (a lower level).</summary>
    private bool IsFed(int x, int y, int z, int level, bool waterAbove)
    {
        if (waterAbove) return true;
        foreach (var (dx, dz) in Horizontal)
            if (_world.IsWater(x + dx, y, z + dz) && _world.GetFluidLevel(x + dx, y, z + dz) < level) return true;
        return false;
    }
}
