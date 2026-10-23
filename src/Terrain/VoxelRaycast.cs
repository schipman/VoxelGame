using Microsoft.Xna.Framework;
using VoxelGame.Blocks;

namespace VoxelGame.Terrain;

/// <summary>A block hit by a ray, plus the face it was hit on (the normal points out toward the ray origin).</summary>
public readonly record struct BlockHit(int X, int Y, int Z, int NormalX, int NormalY, int NormalZ)
{
    /// <summary>The empty block next to the hit face: where a placed block goes.</summary>
    public (int X, int Y, int Z) PlacePosition => (X + NormalX, Y + NormalY, Z + NormalZ);
}

public static class VoxelRaycast
{
    /// <summary>
    /// Walks a ray through the block grid (Amanatides &amp; Woo DDA) and returns the first non-air block
    /// within maxDistance, or null. Visits only the blocks the ray actually passes through.
    /// </summary>
    public static BlockHit? Cast(World world, Vector3 origin, Vector3 direction, float maxDistance)
    {
        direction.Normalize();

        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);

        // Ray starts inside a block: report it with no face, so nothing can be placed against it.
        if (world.GetBlock(x, y, z) != BlockType.Air) return new BlockHit(x, y, z, 0, 0, 0);

        int stepX = Math.Sign(direction.X), stepY = Math.Sign(direction.Y), stepZ = Math.Sign(direction.Z);
        float tDeltaX = stepX == 0 ? float.MaxValue : 1f / MathF.Abs(direction.X);
        float tDeltaY = stepY == 0 ? float.MaxValue : 1f / MathF.Abs(direction.Y);
        float tDeltaZ = stepZ == 0 ? float.MaxValue : 1f / MathF.Abs(direction.Z);

        // Distance along the ray to the first block boundary on each axis.
        float tMaxX = stepX == 0 ? float.MaxValue : (stepX > 0 ? x + 1 - origin.X : origin.X - x) * tDeltaX;
        float tMaxY = stepY == 0 ? float.MaxValue : (stepY > 0 ? y + 1 - origin.Y : origin.Y - y) * tDeltaY;
        float tMaxZ = stepZ == 0 ? float.MaxValue : (stepZ > 0 ? z + 1 - origin.Z : origin.Z - z) * tDeltaZ;

        while (true)
        {
            int nx = 0, ny = 0, nz = 0;
            float t;

            if (tMaxX < tMaxY && tMaxX < tMaxZ)      { t = tMaxX; x += stepX; tMaxX += tDeltaX; nx = -stepX; }
            else if (tMaxY < tMaxZ)                  { t = tMaxY; y += stepY; tMaxY += tDeltaY; ny = -stepY; }
            else                                     { t = tMaxZ; z += stepZ; tMaxZ += tDeltaZ; nz = -stepZ; }

            if (t > maxDistance) return null;
            if (world.GetBlock(x, y, z) != BlockType.Air) return new BlockHit(x, y, z, nx, ny, nz);
        }
    }
}
