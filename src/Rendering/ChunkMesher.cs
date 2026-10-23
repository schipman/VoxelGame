using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;
using VoxelGame.Terrain;

namespace VoxelGame.Rendering;

/// <summary>
/// Converts a chunk's blocks into triangles. Only faces that touch a non-opaque neighbour are emitted,
/// so buried faces cost nothing. Each vertex also gets baked lighting:
///   - smooth light: the average of the (up to) four light cells around that corner of the face
///   - ambient occlusion: darkening where the corner is tucked against neighbouring blocks
/// </summary>
public static class ChunkMesher
{
    private enum Slot { Top, Side, Bottom }

    // Direction to the neighbouring block, which tile slot the face uses, then 4 corners and their
    // unit UVs. Corners are wound clockwise as seen from outside, which is MonoGame's front-face convention.
    private sealed record Face(int Dx, int Dy, int Dz, Slot Slot, Vector3[] Corners, Vector2[] UVs);

    private static readonly Vector2[] UvTop    = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
    private static readonly Vector2[] UvBottom = { new(0, 0), new(0, 1), new(1, 1), new(1, 0) };
    private static readonly Vector2[] UvSideA  = { new(1, 1), new(0, 1), new(0, 0), new(1, 0) };
    private static readonly Vector2[] UvSideB  = { new(0, 1), new(0, 0), new(1, 0), new(1, 1) };

    private static readonly Face[] Faces =
    {
        new( 0, +1,  0, Slot.Top,    new Vector3[] { new(0,1,0), new(1,1,0), new(1,1,1), new(0,1,1) }, UvTop),
        new( 0, -1,  0, Slot.Bottom, new Vector3[] { new(0,0,0), new(0,0,1), new(1,0,1), new(1,0,0) }, UvBottom),
        new(+1,  0,  0, Slot.Side,   new Vector3[] { new(1,0,0), new(1,0,1), new(1,1,1), new(1,1,0) }, UvSideA),
        new(-1,  0,  0, Slot.Side,   new Vector3[] { new(0,0,0), new(0,1,0), new(0,1,1), new(0,0,1) }, UvSideB),
        new( 0,  0, +1, Slot.Side,   new Vector3[] { new(0,0,1), new(0,1,1), new(1,1,1), new(1,0,1) }, UvSideB),
        new( 0,  0, -1, Slot.Side,   new Vector3[] { new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0) }, UvSideA),
    };

    // Ambient occlusion brightness for 0..3 (3 = open corner, 0 = fully enclosed).
    private static readonly float[] AoBrightness = { 0.45f, 0.65f, 0.82f, 1.0f };

    // Reused between calls to avoid per-chunk allocations (meshing happens on one thread).
    private static readonly List<VoxelVertex> Vertices = new();
    private static readonly List<int> Indices = new();
    private static readonly float[] CornerAo = new float[4];
    private static readonly Color[] CornerLight = new Color[4];

    public static void Build(Chunk chunk, World world, GraphicsDevice device)
    {
        Vertices.Clear();
        Indices.Clear();

        for (int y = 0; y < Chunk.Height; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            var type = chunk.Get(x, y, z);
            if (type == BlockType.Air) continue;
            var def = BlockRegistry.Get(type);

            foreach (var face in Faces)
            {
                var neighbour = GetNeighbour(chunk, world, x + face.Dx, y + face.Dy, z + face.Dz);
                if (BlockRegistry.IsOpaque(neighbour)) continue;
                if (neighbour == type && BlockRegistry.CullsSameNeighbor(type)) continue;   // inside a canopy

                var tile = face.Slot switch { Slot.Top => def.Top, Slot.Bottom => def.Bottom, _ => def.Side };
                AddFace(face, tile, def.LightEmission, chunk, world, x, y, z);
            }
        }

        chunk.SetMesh(device, Vertices, Indices);
    }

    /// <summary>Stays inside the chunk array when it can and asks the world only across chunk edges.</summary>
    private static BlockType GetNeighbour(Chunk chunk, World world, int x, int y, int z)
    {
        if (y < 0 || y >= Chunk.Height) return BlockType.Air;
        if (x >= 0 && x < Chunk.Size && z >= 0 && z < Chunk.Size) return chunk.Get(x, y, z);
        return world.GetBlock(chunk.WorldX + x, y, chunk.WorldZ + z);
    }

    /// <summary>Reads block type and both light levels at chunk-local coordinates (which may lie outside the chunk).</summary>
    private static void Sample(Chunk chunk, World world, int x, int y, int z, out bool opaque, out int sky, out int block)
    {
        if (x >= 0 && x < Chunk.Size && z >= 0 && z < Chunk.Size && y >= 0 && y < Chunk.Height)
        {
            opaque = BlockRegistry.IsOpaque(chunk.Get(x, y, z));
            sky = chunk.GetSkyLight(x, y, z);
            block = chunk.GetBlockLight(x, y, z);
            return;
        }
        world.GetCell(chunk.WorldX + x, y, chunk.WorldZ + z, out var type, out sky, out block);
        opaque = BlockRegistry.IsOpaque(type);
    }

    private static void AddFace(Face face, Tile tile, int emission, Chunk chunk, World world, int x, int y, int z)
    {
        // The two axes that lie in the plane of this face (0 = x, 1 = y, 2 = z).
        int axisU = face.Dx != 0 ? 1 : 0;
        int axisV = face.Dz != 0 ? 1 : 2;

        // Light and occlusion come from the cells in front of the face.
        int fx = x + face.Dx, fy = y + face.Dy, fz = z + face.Dz;

        for (int i = 0; i < 4; i++)
        {
            var corner = face.Corners[i];
            // Which way this corner leans along each in-plane axis: -1 or +1.
            int su = Component(corner, axisU) > 0.5f ? 1 : -1;
            int sv = Component(corner, axisV) > 0.5f ? 1 : -1;

            int ux = axisU == 0 ? su : 0, uy = axisU == 1 ? su : 0, uz = axisU == 2 ? su : 0;
            int vx = axisV == 0 ? sv : 0, vy = axisV == 1 ? sv : 0, vz = axisV == 2 ? sv : 0;

            Sample(chunk, world, fx,           fy,           fz,           out _,        out int skyC, out int blkC);
            Sample(chunk, world, fx + ux,      fy + uy,      fz + uz,      out bool oU,  out int skyU, out int blkU);
            Sample(chunk, world, fx + vx,      fy + vy,      fz + vz,      out bool oV,  out int skyV, out int blkV);
            Sample(chunk, world, fx + ux + vx, fy + uy + vy, fz + uz + vz, out bool oUV, out int skyUV, out int blkUV);

            // Ambient occlusion: two solid sides fully close the corner; otherwise count solid neighbours.
            int ao = oU && oV ? 0 : 3 - ((oU ? 1 : 0) + (oV ? 1 : 0) + (oUV ? 1 : 0));
            CornerAo[i] = AoBrightness[ao];

            // Smooth light: average the open cells around the corner. A corner cell behind two solid sides is ignored.
            int count = 1, sky = skyC, blk = blkC;
            if (!oU)                     { count++; sky += skyU;  blk += blkU; }
            if (!oV)                     { count++; sky += skyV;  blk += blkV; }
            if (!oUV && !(oU && oV))     { count++; sky += skyUV; blk += blkUV; }

            CornerLight[i] = new Color(
                sky / (count * (float)LightingEngine.MaxLight),
                blk / (count * (float)LightingEngine.MaxLight),
                CornerAo[i],
                emission / (float)LightingEngine.MaxLight);
        }

        var normal = new Vector3(face.Dx, face.Dy, face.Dz);
        var origin = new Vector3(chunk.WorldX + x, y, chunk.WorldZ + z);
        int start = Vertices.Count;
        for (int i = 0; i < 4; i++)
            Vertices.Add(new VoxelVertex(origin + face.Corners[i], normal, TextureAtlas.MapUV(tile, face.UVs[i]), CornerLight[i]));

        // Split the quad along the diagonal that keeps the occlusion gradient smooth (avoids the classic AO "seam").
        if (CornerAo[0] + CornerAo[2] > CornerAo[1] + CornerAo[3])
        {
            Indices.Add(start + 1); Indices.Add(start + 2); Indices.Add(start + 3);
            Indices.Add(start + 1); Indices.Add(start + 3); Indices.Add(start);
        }
        else
        {
            Indices.Add(start); Indices.Add(start + 1); Indices.Add(start + 2);
            Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 3);
        }
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
