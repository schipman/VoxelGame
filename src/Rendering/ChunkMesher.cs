using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;
using VoxelGame.Terrain;

namespace VoxelGame.Rendering;

/// <summary>
/// Converts a chunk's blocks into triangles, in two batches: solid blocks, and water.
///
/// Solid blocks only get the faces that touch a non-opaque neighbour, so buried faces cost nothing. Each vertex
/// also gets baked lighting:
///   - smooth light: the average of the (up to) four light cells around that corner of the face
///   - ambient occlusion: darkening where the corner is tucked against neighbouring blocks
/// Faces under water also record how deep they are, which the shader uses for caustics.
///
/// Water is drawn separately, with a surface whose height follows the fluid level (full for a source block,
/// lower the further it has flowed) and is blended between neighbouring cells so the surface has no steps or cracks.
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
    private static readonly List<VoxelVertex> WaterVertices = new();
    private static readonly List<int> WaterIndices = new();
    private static readonly float[] CornerAo = new float[4];
    private static readonly Color[] CornerLight = new Color[4];

    public static void Build(Chunk chunk, World world, GraphicsDevice device)
    {
        Vertices.Clear();
        Indices.Clear();
        WaterVertices.Clear();
        WaterIndices.Clear();

        for (int y = 0; y < Chunk.Height; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            var type = chunk.Get(x, y, z);
            if (type == BlockType.Air) continue;

            if (BlockRegistry.IsLiquid(type))
            {
                AddWater(chunk, world, x, y, z);
                continue;
            }

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

        chunk.SetMeshes(device, Vertices, Indices, WaterVertices, WaterIndices);
    }

    /// <summary>Stays inside the chunk array when it can and asks the world only across chunk edges.</summary>
    private static BlockType GetNeighbour(Chunk chunk, World world, int x, int y, int z)
    {
        if (y < 0 || y >= Chunk.Height) return BlockType.Air;
        if (x >= 0 && x < Chunk.Size && z >= 0 && z < Chunk.Size) return chunk.Get(x, y, z);
        return world.GetBlock(chunk.WorldX + x, y, chunk.WorldZ + z);
    }

    private static int GetFluidLevel(Chunk chunk, World world, int x, int y, int z)
    {
        if (y < 0 || y >= Chunk.Height) return 0;
        if (x >= 0 && x < Chunk.Size && z >= 0 && z < Chunk.Size) return chunk.GetFluidLevel(x, y, z);
        return world.GetFluidLevel(chunk.WorldX + x, y, chunk.WorldZ + z);
    }

    /// <summary>Reads block type and both light levels at chunk-local coordinates (which may lie outside the chunk).</summary>
    private static void Sample(Chunk chunk, World world, int x, int y, int z, out BlockType type, out int sky, out int block)
    {
        if (x >= 0 && x < Chunk.Size && z >= 0 && z < Chunk.Size && y >= 0 && y < Chunk.Height)
        {
            type = chunk.Get(x, y, z);
            sky = chunk.GetSkyLight(x, y, z);
            block = chunk.GetBlockLight(x, y, z);
            return;
        }
        world.GetCell(chunk.WorldX + x, y, chunk.WorldZ + z, out type, out sky, out block);
    }

    // ---------------------------------------------------------------- solid blocks

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

            Sample(chunk, world, fx,           fy,           fz,           out _,          out int skyC, out int blkC);
            Sample(chunk, world, fx + ux,      fy + uy,      fz + uz,      out var tU,     out int skyU, out int blkU);
            Sample(chunk, world, fx + vx,      fy + vy,      fz + vz,      out var tV,     out int skyV, out int blkV);
            Sample(chunk, world, fx + ux + vx, fy + uy + vy, fz + uz + vz, out var tUV,    out int skyUV, out int blkUV);
            bool oU = BlockRegistry.IsOpaque(tU), oV = BlockRegistry.IsOpaque(tV), oUV = BlockRegistry.IsOpaque(tUV);

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

        // If the face looks into water, remember how deep it is so the shader can add caustics and fade the light.
        var extra = Color.Transparent;
        Sample(chunk, world, fx, fy, fz, out var frontType, out _, out _);
        if (BlockRegistry.IsLiquid(frontType))
        {
            int depth = 0, top = fy;
            while (depth < 15 && IsWater(chunk, world, fx, top, fz)) { depth++; top++; }
            Sample(chunk, world, fx, top, fz, out _, out int surfaceSky, out _);
            extra = new Color(depth / 15f, surfaceSky / (float)LightingEngine.MaxLight, 0f, 0f);
        }

        var normal = new Vector3(face.Dx, face.Dy, face.Dz);
        var origin = new Vector3(chunk.WorldX + x, y, chunk.WorldZ + z);
        int start = Vertices.Count;
        for (int i = 0; i < 4; i++)
            Vertices.Add(new VoxelVertex(origin + face.Corners[i], normal, TextureAtlas.MapUV(tile, face.UVs[i]), CornerLight[i], extra));

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

    private static bool IsWater(Chunk chunk, World world, int x, int y, int z) =>
        BlockRegistry.IsLiquid(GetNeighbour(chunk, world, x, y, z));

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // ---------------------------------------------------------------- water

    /// <summary>Height of a water cell's surface, as a fraction of a block: source blocks sit just under the brim, flowing water is lower.</summary>
    private static float LevelHeight(int level) => FluidSimulator.SurfaceHeight(level);

    /// <summary>
    /// Height of the water surface at one corner of a cell: the average of the water cells that meet at that corner.
    /// Neighbouring cells share corners, so their surfaces join up exactly. A cell with water above it is full height.
    /// </summary>
    private static float CornerHeight(Chunk chunk, World world, int x, int y, int z, int cornerX, int cornerZ)
    {
        float sum = 0f;
        int count = 0;
        for (int dx = cornerX - 1; dx <= cornerX; dx++)
        for (int dz = cornerZ - 1; dz <= cornerZ; dz++)
        {
            var type = GetNeighbour(chunk, world, x + dx, y, z + dz);
            if (!BlockRegistry.IsLiquid(type)) continue;                       // air and solids don't pull the surface down
            if (IsWater(chunk, world, x + dx, y + 1, z + dz)) return 1f;       // water continues upward: full height
            sum += LevelHeight(GetFluidLevel(chunk, world, x + dx, y, z + dz));
            count++;
        }
        return count == 0 ? LevelHeight(0) : sum / count;
    }

    private static void AddWater(Chunk chunk, World world, int x, int y, int z)
    {
        bool surface = !IsWater(chunk, world, x, y + 1, z);
        var above = GetNeighbour(chunk, world, x, y + 1, z);

        // Surface height at the four corners (index: i + 2 * j for corner (i, j)).
        Span<float> top = stackalloc float[4];
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
                top[i + 2 * j] = surface ? CornerHeight(chunk, world, x, y, z, i, j) : 1f;

        var flow = FlowDirection(chunk, world, x, y, z);
        float surfaceMask = surface ? 1f : 0f;
        var origin = new Vector3(chunk.WorldX + x, y, chunk.WorldZ + z);

        // Top: only where the water meets air (or something see-through), never under a roof of stone.
        if (surface && !BlockRegistry.IsOpaque(above))
        {
            Sample(chunk, world, x, y + 1, z, out _, out int sky, out int blk);
            var light = new Color(sky / 15f, blk / 15f, 1f, 0f);
            AddWaterQuad(light, new Vector3(0, 1, 0), surfaceMask, flow,
                origin + new Vector3(0, top[0], 0), origin + new Vector3(1, top[1], 0),
                origin + new Vector3(1, top[3], 1), origin + new Vector3(0, top[2], 1));
        }

        // Walls: only where the neighbour is see-through and not itself water (water-to-water needs no wall).
        for (int d = 0; d < 4; d++)
        {
            int dx = d == 0 ? 1 : d == 1 ? -1 : 0, dz = d == 2 ? 1 : d == 3 ? -1 : 0;
            var neighbour = GetNeighbour(chunk, world, x + dx, y, z + dz);
            if (BlockRegistry.IsOpaque(neighbour) || BlockRegistry.IsLiquid(neighbour)) continue;

            Sample(chunk, world, x + dx, y, z + dz, out _, out int sky, out int blk);
            var light = new Color(sky / 15f, blk / 15f, 1f, 0f);
            var normal = new Vector3(dx, 0, dz);
            // The two top corners on this side; the bottom edge sits on the cell floor.
            var a = d switch { 0 => (1, 0), 1 => (0, 0), 2 => (0, 1), _ => (0, 0) };
            var b = d switch { 0 => (1, 1), 1 => (0, 1), 2 => (1, 1), _ => (1, 0) };
            float ha = top[a.Item1 + 2 * a.Item2], hb = top[b.Item1 + 2 * b.Item2];
            var pa = origin + new Vector3(a.Item1, 0, a.Item2);
            var pb = origin + new Vector3(b.Item1, 0, b.Item2);
            AddWaterQuad(light, normal, surfaceMask, flow,
                pa, pb, pb + new Vector3(0, hb, 0), pa + new Vector3(0, ha, 0), topIndices: (2, 3));
        }

        // Bottom: only if the water is open to the air underneath (a waterfall's underside).
        var below = GetNeighbour(chunk, world, x, y - 1, z);
        if (!BlockRegistry.IsOpaque(below) && !BlockRegistry.IsLiquid(below))
        {
            Sample(chunk, world, x, y - 1, z, out _, out int sky, out int blk);
            var light = new Color(sky / 15f, blk / 15f, 1f, 0f);
            AddWaterQuad(light, new Vector3(0, -1, 0), 0f, flow,
                origin, origin + new Vector3(0, 0, 1), origin + new Vector3(1, 0, 1), origin + new Vector3(1, 0, 0), topIndices: (-1, -1));
        }
    }

    /// <summary>
    /// Adds a quad to the water mesh. Corners are given in winding order. <paramref name="surfaceMask"/> is written
    /// to the vertices listed in <paramref name="topIndices"/> (default: all four), marking them as part of the
    /// water surface so the shader moves them with the waves.
    /// </summary>
    private static void AddWaterQuad(Color light, Vector3 normal, float surfaceMask, Vector2 flow,
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, (int, int)? topIndices = null)
    {
        var corners = new[] { p0, p1, p2, p3 };
        int start = WaterVertices.Count;
        for (int i = 0; i < 4; i++)
        {
            bool isTop = topIndices is null || topIndices.Value.Item1 == i || topIndices.Value.Item2 == i;
            var extra = new Color(isTop ? surfaceMask : 0f, flow.X * 0.5f + 0.5f, flow.Y * 0.5f + 0.5f, 0f);
            WaterVertices.Add(new VoxelVertex(corners[i], normal, Vector2.Zero, light, extra));
        }
        WaterIndices.Add(start); WaterIndices.Add(start + 1); WaterIndices.Add(start + 2);
        WaterIndices.Add(start); WaterIndices.Add(start + 2); WaterIndices.Add(start + 3);
    }

    /// <summary>
    /// Which way the water moves, as a unit vector (zero if still). Rivers carry their direction from the generator;
    /// flowing water runs from low levels (near the source) toward high ones.
    /// </summary>
    private static Vector2 FlowDirection(Chunk chunk, World world, int x, int y, int z)
    {
        int level = chunk.GetFluidLevel(x, y, z);
        if (level == 0)
        {
            var river = chunk.GetFlow(x, z);
            if (river != Vector2.Zero) return river;
        }

        var sum = Vector2.Zero;
        for (int d = 0; d < 4; d++)
        {
            int dx = d == 0 ? 1 : d == 1 ? -1 : 0, dz = d == 2 ? 1 : d == 3 ? -1 : 0;
            if (!IsWater(chunk, world, x + dx, y, z + dz)) continue;
            int diff = GetFluidLevel(chunk, world, x + dx, y, z + dz) - level;
            sum += new Vector2(dx, dz) * diff;
        }
        return sum.LengthSquared() < 1e-4f ? Vector2.Zero : Vector2.Normalize(sum);
    }
}
