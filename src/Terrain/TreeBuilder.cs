using VoxelGame.Blocks;

namespace VoxelGame.Terrain;

/// <summary>
/// Shapes of the things that grow on the ground. Each builder reports blocks one at a time through a callback,
/// so the same code works whichever chunk(s) the tree overlaps; the caller decides which blocks fall in its chunk.
/// All variation comes from the hash passed in, so a tree always looks the same for a given seed.
/// </summary>
public static class TreeBuilder
{
    public delegate void Place(int x, int y, int z, BlockType block);

    /// <summary>How far (in blocks, horizontally) any tree can reach from its base position.</summary>
    public const int MaxReach = 5;

    public static bool IsLeaves(BlockType type) =>
        type is BlockType.OakLeaves or BlockType.BirchLeaves or BlockType.SpruceLeaves
             or BlockType.DarkOakLeaves or BlockType.PaleOakLeaves or BlockType.AcaciaLeaves;

    /// <summary>Builds a tree whose trunk stands on the block below (x, y, z).</summary>
    public static void Build(TreeKind kind, int x, int y, int z, uint hash, Place place)
    {
        switch (kind)
        {
            case TreeKind.Oak:     Rounded(x, y, z, hash, 4 + (int)(hash % 3), BlockType.OakLog, BlockType.OakLeaves, place); break;
            case TreeKind.Birch:   Rounded(x, y, z, hash, 5 + (int)(hash % 3), BlockType.BirchLog, BlockType.BirchLeaves, place); break;
            case TreeKind.Spruce:  Spruce(x, y, z, 6 + (int)(hash % 4), place); break;
            case TreeKind.DarkOak: Broad(x, y, z, hash, 5 + (int)(hash % 3), BlockType.DarkOakLog, BlockType.DarkOakLeaves, 0f, place); break;
            case TreeKind.PaleOak: Broad(x, y, z, hash, 6 + (int)(hash % 3), BlockType.PaleOakLog, BlockType.PaleOakLeaves, 0.22f, place); break;
            case TreeKind.Acacia:  Acacia(x, y, z, hash, place); break;
            case TreeKind.Cactus:
                for (int i = 0, h = 1 + (int)(hash % 3); i < h; i++) place(x, y + i, z, BlockType.Cactus);
                break;
        }
    }

    /// <summary>Oak and birch: a straight trunk under a rounded blob of leaves.</summary>
    private static void Rounded(int x, int y, int z, uint hash, int height, BlockType log, BlockType leaves, Place place)
    {
        for (int i = 0; i < height; i++) place(x, y + i, z, log);

        int top = y + height - 1;
        Square(x, top - 1, z, 2, true, hash, leaves, place);
        Square(x, top,     z, 2, true, hash, leaves, place);
        Square(x, top + 1, z, 1, false, hash, leaves, place);
        Square(x, top + 2, z, 1, true, hash, leaves, place, plusShape: true);
    }

    /// <summary>A cone of leaves that widens by one block every two layers.</summary>
    private static void Spruce(int x, int y, int z, int height, Place place)
    {
        for (int i = 0; i < height; i++) place(x, y + i, z, BlockType.SpruceLog);

        int top = y + height;   // the tip, one above the last log
        for (int layer = 0; top - layer >= y + 2; layer++)
        {
            int r = layer == 0 ? 0 : Math.Min(3, (layer + 1) / 2);
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
                if (dx * dx + dz * dz <= r * r + (r > 0 ? 1 : 0))
                    place(x + dx, top - layer, z + dz, BlockType.SpruceLeaves);
        }
    }

    /// <summary>Dark oak and pale oak: a thick 2x2 trunk under a wide flat canopy. Sparsity punches holes in the leaves.</summary>
    private static void Broad(int x, int y, int z, uint hash, int height, BlockType log, BlockType leaves, float sparsity, Place place)
    {
        for (int i = 0; i < height; i++)
        {
            place(x, y + i, z, log); place(x + 1, y + i, z, log);
            place(x, y + i, z + 1, log); place(x + 1, y + i, z + 1, log);
        }

        int top = y + height - 1;
        // Canopy layers by height, each a disc around the centre of the 2x2 trunk.
        Disc(x, top - 2, z, 3.2f, hash, leaves, sparsity, place);
        Disc(x, top - 1, z, 10.5f, hash, leaves, sparsity, place);
        Disc(x, top,     z, 10.5f, hash, leaves, sparsity, place);
        Disc(x, top + 1, z, 5.5f, hash, leaves, sparsity, place);
    }

    private static void Disc(int x, int y, int z, float radiusSquared, uint hash, BlockType leaves, float sparsity, Place place)
    {
        for (int dx = -3; dx <= 4; dx++)
        for (int dz = -3; dz <= 4; dz++)
        {
            float fx = dx - 0.5f, fz = dz - 0.5f;   // distance from the middle of the trunk
            float d2 = fx * fx + fz * fz;
            if (d2 > radiusSquared) continue;
            if (d2 > 8f && Chance(hash, x + dx, y, z + dz) < 0.35f) continue;   // ragged edge
            if (sparsity > 0f && Chance(hash, x + dx, y + 1000, z + dz) < sparsity) continue;
            place(x + dx, y, z + dz, leaves);
        }
    }

    /// <summary>A short trunk that bends sideways, with a flat umbrella of leaves at the tip.</summary>
    private static void Acacia(int x, int y, int z, uint hash, Place place)
    {
        int height = 3 + (int)(hash % 2);
        int dir = (int)((hash >> 4) % 4);
        int dx = dir == 0 ? 1 : dir == 1 ? -1 : 0;
        int dz = dir == 2 ? 1 : dir == 3 ? -1 : 0;

        for (int i = 0; i < height; i++) place(x, y + i, z, BlockType.AcaciaLog);
        place(x + dx, y + height, z + dz, BlockType.AcaciaLog);
        place(x + dx, y + height + 1, z + dz, BlockType.AcaciaLog);

        int tx = x + dx, tz = z + dz, ty = y + height + 1;
        Square(tx, ty,     tz, 2, true, hash, BlockType.AcaciaLeaves, place);
        Square(tx, ty + 1, tz, 1, false, hash, BlockType.AcaciaLeaves, place);
    }

    /// <summary>A flat square of leaves centred on (x, z). Corners are either dropped randomly or kept.</summary>
    private static void Square(int x, int y, int z, int radius, bool roundCorners, uint hash, BlockType leaves, Place place, bool plusShape = false)
    {
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            bool corner = Math.Abs(dx) == radius && Math.Abs(dz) == radius;
            if (plusShape && Math.Abs(dx) + Math.Abs(dz) > radius) continue;
            if (corner && roundCorners && Chance(hash, x + dx, y, z + dz) < 0.6f) continue;
            place(x + dx, y, z + dz, leaves);
        }
    }

    // ---- Deterministic randomness ----

    /// <summary>Well-mixed 32-bit hash of a seed and a position.</summary>
    public static uint Hash(int seed, int x, int y, int z)
    {
        uint h = (uint)seed * 0x9E3779B1u;
        h ^= (uint)x * 0x85EBCA6Bu; h = (h << 13) | (h >> 19);
        h ^= (uint)y * 0xC2B2AE35u; h = (h << 11) | (h >> 21);
        h ^= (uint)z * 0x27D4EB2Fu;
        h ^= h >> 16; h *= 0x7FEB352Du;
        h ^= h >> 15; h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    /// <summary>Repeatable pseudo-random number in 0..1 for a position.</summary>
    private static float Chance(uint salt, int x, int y, int z) => (Hash((int)salt, x, y, z) & 0xFFFFFF) / (float)0x1000000;
}
