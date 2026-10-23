using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Terrain;

namespace VoxelGame.Rendering;

/// <summary>
/// Turns a chunk's blocks into triangles, in two batches: solid blocks, and water. Works on plain data (a
/// <see cref="ChunkNeighborhood"/>) and makes no GPU calls, so it can run on any thread; each thread needs its own instance.
///
/// Solid blocks: only faces that touch a non-opaque neighbour are made, and then <b>greedy meshing</b> merges them into
/// as few rectangles as possible. Every visible face is described by its texture tile and the light and ambient
/// occlusion at its four corners (smooth lighting). Following
/// https://0fps.net/2013/07/03/ambient-occlusion-for-minecraft-like-worlds/, faces are merged only if their lighting
/// matches exactly and is the same at all four corners: then one big quad looks identical to the many it replaces.
/// Faces whose corners differ (a shadow edge, a tucked-in corner) stay single quads, split along the diagonal that keeps
/// the gradient smooth. Merged quads repeat their texture: the vertex UV counts tiles (0 to width) and the shader wraps it
/// inside the tile given in the vertex's extra data.
///
/// Water is drawn separately and not merged: the water shader moves its vertices with waves, so it needs the density.
/// </summary>
public sealed class ChunkMesher
{
    private enum Slot { Top, Side, Bottom }

    // ---- The six faces of a block ----

    private sealed class Face
    {
        public readonly int Dx, Dy, Dz;
        public readonly Slot Slot;
        public readonly Vector3[] Corners;       // unit-cube corners, clockwise seen from outside (MonoGame's front-face winding)
        public readonly Vector2[] UVs;           // 0..1 texture coordinate of each corner
        public readonly Vector3 Normal;
        public readonly int NormalAxis;          // 0 = x, 1 = y, 2 = z
        public readonly int AxisU, AxisV;        // the two axes the greedy mask runs over
        public readonly int UvAxisX, UvAxisY;    // which spatial axis each texture coordinate grows along (merged quads scale UVs by that extent)

        public Face(int dx, int dy, int dz, Slot slot, Vector3[] corners, Vector2[] uvs)
        {
            Dx = dx; Dy = dy; Dz = dz; Slot = slot; Corners = corners; UVs = uvs;
            Normal = new Vector3(dx, dy, dz);
            NormalAxis = dx != 0 ? 0 : dy != 0 ? 1 : 2;
            (AxisU, AxisV) = NormalAxis switch { 0 => (2, 1), 1 => (0, 2), _ => (0, 1) };
            UvAxisX = FindUvAxis(corners, uvs, useX: true, NormalAxis);
            UvAxisY = FindUvAxis(corners, uvs, useX: false, NormalAxis);
        }

        /// <summary>Finds the spatial axis a UV component follows: the one whose corner coordinate equals the UV (or its inverse) at every corner.</summary>
        private static int FindUvAxis(Vector3[] corners, Vector2[] uvs, bool useX, int normalAxis)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == normalAxis) continue;
                bool same = true, inverse = true;
                for (int i = 0; i < 4; i++)
                {
                    float c = Component(corners[i], axis), uv = useX ? uvs[i].X : uvs[i].Y;
                    same &= MathF.Abs(uv - c) < 1e-4f;
                    inverse &= MathF.Abs(uv - (1 - c)) < 1e-4f;
                }
                if (same || inverse) return axis;
            }
            throw new InvalidOperationException("texture coordinates don't follow an axis");
        }
    }

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

    // ---- One visible face, ready to be merged ----

    private struct FaceCell
    {
        public bool Present;
        public bool Uniform;                 // all four corners have identical light: safe to merge
        public Color L0, L1, L2, L3;         // per-corner light: R sky, G block, B ambient occlusion, A emissive
        public Color Extra;                  // R water depth, G surface light, B tile column, A tile row
    }

    // ---- State (one set per mesher, so each thread needs its own) ----

    private readonly List<VoxelVertex> _vertices = new();
    private readonly List<int> _indices = new();
    private readonly List<VoxelVertex> _waterVertices = new();
    private readonly List<int> _waterIndices = new();
    private readonly FaceCell[] _mask = new FaceCell[Chunk.Size * Chunk.Height];
    private readonly Color[] _corner = new Color[4];

    private ChunkNeighborhood _n;

    /// <summary>Builds the solid and water meshes for the centre chunk of a neighbourhood.</summary>
    public ChunkMeshData Build(ChunkNeighborhood neighbourhood, out ChunkMeshData water)
    {
        _n = neighbourhood;
        _vertices.Clear();
        _indices.Clear();
        _waterVertices.Clear();
        _waterIndices.Clear();

        var chunk = neighbourhood.Center;
        int top = Math.Min(chunk.TopY + 1, Chunk.Height);   // one past the highest block, so its top faces are seen

        foreach (var face in Faces) MeshDirection(chunk, face, top);
        MeshWater(chunk, top);

        var solid = Finish(_vertices, _indices);
        water = Finish(_waterVertices, _waterIndices);
        _n = null;
        return solid;
    }

    private static ChunkMeshData Finish(List<VoxelVertex> vertices, List<int> indices) =>
        indices.Count == 0 ? ChunkMeshData.Empty : new ChunkMeshData(vertices.ToArray(), indices.ToArray());

    // ---------------------------------------------------------------- solid blocks

    /// <summary>Meshes every visible face that points one way, slice by slice, merging neighbouring faces into rectangles.</summary>
    private void MeshDirection(Chunk chunk, Face face, int top)
    {
        Span<int> size = stackalloc int[] { Chunk.Size, top, Chunk.Size };
        int sliceCount = size[face.NormalAxis], uSize = size[face.AxisU], vSize = size[face.AxisV];
        Span<int> pos = stackalloc int[3];

        for (int slice = 0; slice < sliceCount; slice++)
        {
            // 1. Describe every visible face in this slice.
            bool any = false;
            pos[face.NormalAxis] = slice;
            for (int v = 0; v < vSize; v++)
            for (int u = 0; u < uSize; u++)
            {
                pos[face.AxisU] = u;
                pos[face.AxisV] = v;
                ref var cell = ref _mask[v * uSize + u];

                var type = chunk.Get(pos[0], pos[1], pos[2]);
                if (type == BlockType.Air || BlockRegistry.IsLiquid(type) || !DescribeFace(face, type, pos[0], pos[1], pos[2], ref cell))
                    cell.Present = false;
                else
                    any = true;
            }
            if (!any) continue;

            // 2. Merge: grow each face right, then down, while the next ones match.
            for (int v = 0; v < vSize; v++)
            for (int u = 0; u < uSize; u++)
            {
                var cell = _mask[v * uSize + u];
                if (!cell.Present) continue;

                int w = 1, h = 1;
                if (cell.Uniform)
                {
                    while (u + w < uSize && CanMerge(in cell, in _mask[v * uSize + u + w])) w++;

                    for (bool grow = true; grow && v + h < vSize;)
                    {
                        for (int k = 0; k < w && grow; k++) grow = CanMerge(in cell, in _mask[(v + h) * uSize + u + k]);
                        if (grow) h++;
                    }
                }

                for (int dv = 0; dv < h; dv++)
                    for (int du = 0; du < w; du++)
                        _mask[(v + dv) * uSize + u + du].Present = false;

                pos[face.NormalAxis] = slice;
                pos[face.AxisU] = u;
                pos[face.AxisV] = v;
                EmitQuad(chunk, face, pos[0], pos[1], pos[2], w, h, in cell);
            }
        }
    }

    private static bool CanMerge(in FaceCell a, in FaceCell b) =>
        b.Present && b.Uniform && a.L0 == b.L0 && a.Extra == b.Extra;

    /// <summary>
    /// Works out whether the face of a block is visible and, if so, its light at each corner. Returns false if hidden
    /// (the neighbour is opaque, or a leaf block touching another leaf block).
    /// </summary>
    private bool DescribeFace(Face face, BlockType type, int x, int y, int z, ref FaceCell cell)
    {
        // Light and occlusion come from the cells in front of the face.
        int fx = x + face.Dx, fy = y + face.Dy, fz = z + face.Dz;
        var front = _n.GetBlock(fx, fy, fz);
        if (BlockRegistry.IsOpaque(front)) return false;
        if (front == type && BlockRegistry.CullsSameNeighbor(type)) return false;

        var def = BlockRegistry.Get(type);
        var tile = face.Slot switch { Slot.Top => def.Top, Slot.Bottom => def.Bottom, _ => def.Side };
        int emission = def.LightEmission;

        // The two axes that lie in the plane of the face.
        int axisU = face.NormalAxis == 0 ? 1 : 0;
        int axisV = face.NormalAxis == 2 ? 1 : 2;

        for (int i = 0; i < 4; i++)
        {
            var corner = face.Corners[i];
            // Which way this corner leans along each in-plane axis: -1 or +1.
            int su = Component(corner, axisU) > 0.5f ? 1 : -1;
            int sv = Component(corner, axisV) > 0.5f ? 1 : -1;

            int ux = axisU == 0 ? su : 0, uy = axisU == 1 ? su : 0, uz = axisU == 2 ? su : 0;
            int vx = axisV == 0 ? sv : 0, vy = axisV == 1 ? sv : 0, vz = axisV == 2 ? sv : 0;

            _n.GetCell(fx,           fy,           fz,           out _,      out int skyC,  out int blkC);
            _n.GetCell(fx + ux,      fy + uy,      fz + uz,      out var tU, out int skyU,  out int blkU);
            _n.GetCell(fx + vx,      fy + vy,      fz + vz,      out var tV, out int skyV,  out int blkV);
            _n.GetCell(fx + ux + vx, fy + uy + vy, fz + uz + vz, out var tUV, out int skyUV, out int blkUV);
            bool oU = BlockRegistry.IsOpaque(tU), oV = BlockRegistry.IsOpaque(tV), oUV = BlockRegistry.IsOpaque(tUV);

            // Ambient occlusion: two solid sides fully close the corner; otherwise count solid neighbours.
            int ao = oU && oV ? 0 : 3 - ((oU ? 1 : 0) + (oV ? 1 : 0) + (oUV ? 1 : 0));

            // Smooth light: average the open cells around the corner. A corner cell behind two solid sides is ignored.
            int count = 1, sky = skyC, blk = blkC;
            if (!oU)                 { count++; sky += skyU;  blk += blkU; }
            if (!oV)                 { count++; sky += skyV;  blk += blkV; }
            if (!oUV && !(oU && oV)) { count++; sky += skyUV; blk += blkUV; }

            _corner[i] = new Color(
                sky / (count * (float)LightingEngine.MaxLight),
                blk / (count * (float)LightingEngine.MaxLight),
                AoBrightness[ao],
                emission / (float)LightingEngine.MaxLight);
        }

        // If the face looks into water, remember how deep it is so the shader can add caustics and fade the light.
        int depthByte = 0, surfaceByte = 0;
        if (BlockRegistry.IsLiquid(front))
        {
            int depth = 0, topY = fy;
            while (depth < 15 && BlockRegistry.IsLiquid(_n.GetBlock(fx, topY, fz))) { depth++; topY++; }
            _n.GetCell(fx, topY, fz, out _, out int surfaceSky, out _);
            depthByte = depth * 255 / 15;
            surfaceByte = surfaceSky * 255 / LightingEngine.MaxLight;
        }

        int t = (int)tile;
        cell.Present = true;
        cell.L0 = _corner[0]; cell.L1 = _corner[1]; cell.L2 = _corner[2]; cell.L3 = _corner[3];
        cell.Uniform = cell.L0 == cell.L1 && cell.L0 == cell.L2 && cell.L0 == cell.L3;
        cell.Extra = new Color(depthByte, surfaceByte, t % TextureAtlas.TilesPerRow, t / TextureAtlas.TilesPerRow);
        return true;
    }

    /// <summary>Adds one rectangle of w x h identical faces (w along the mask's U axis, h along V).</summary>
    private void EmitQuad(Chunk chunk, Face face, int x, int y, int z, int w, int h, in FaceCell cell)
    {
        Span<int> extent = stackalloc int[3];
        extent[face.NormalAxis] = 1;
        extent[face.AxisU] = w;
        extent[face.AxisV] = h;

        var origin = new Vector3(chunk.WorldX + x, y, chunk.WorldZ + z);
        var lights = new[] { cell.L0, cell.L1, cell.L2, cell.L3 };

        int start = _vertices.Count;
        for (int i = 0; i < 4; i++)
        {
            var c = face.Corners[i];
            var position = origin + new Vector3(c.X * extent[0], c.Y * extent[1], c.Z * extent[2]);
            var uv = new Vector2(face.UVs[i].X * extent[face.UvAxisX], face.UVs[i].Y * extent[face.UvAxisY]);
            _vertices.Add(new VoxelVertex(position, face.Normal, uv, lights[i], cell.Extra));
        }

        // A single face with a lighting gradient: split it along the diagonal that keeps the gradient smooth
        // (avoids the classic AO "seam"). Merged quads are uniform, so either split is the same.
        if (cell.L0.B + cell.L2.B > cell.L1.B + cell.L3.B)
        {
            _indices.Add(start + 1); _indices.Add(start + 2); _indices.Add(start + 3);
            _indices.Add(start + 1); _indices.Add(start + 3); _indices.Add(start);
        }
        else
        {
            _indices.Add(start); _indices.Add(start + 1); _indices.Add(start + 2);
            _indices.Add(start); _indices.Add(start + 2); _indices.Add(start + 3);
        }
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // ---------------------------------------------------------------- water

    private void MeshWater(Chunk chunk, int top)
    {
        for (int y = 0; y < top; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
            if (BlockRegistry.IsLiquid(chunk.Get(x, y, z))) AddWater(chunk, x, y, z);
    }

    private bool IsWater(int x, int y, int z) => BlockRegistry.IsLiquid(_n.GetBlock(x, y, z));

    /// <summary>
    /// Height of the water surface at one corner of a cell: the average of the water cells that meet at that corner.
    /// Neighbouring cells share corners, so their surfaces join up exactly. A cell with water above it is full height.
    /// </summary>
    private float CornerHeight(int x, int y, int z, int cornerX, int cornerZ)
    {
        float sum = 0f;
        int count = 0;
        for (int dx = cornerX - 1; dx <= cornerX; dx++)
        for (int dz = cornerZ - 1; dz <= cornerZ; dz++)
        {
            if (!IsWater(x + dx, y, z + dz)) continue;                          // air and solids don't pull the surface down
            if (IsWater(x + dx, y + 1, z + dz)) return 1f;                      // water continues upward: full height
            sum += FluidSimulator.SurfaceHeight(_n.GetFluidLevel(x + dx, y, z + dz));
            count++;
        }
        return count == 0 ? FluidSimulator.SurfaceHeight(0) : sum / count;
    }

    private void AddWater(Chunk chunk, int x, int y, int z)
    {
        bool surface = !IsWater(x, y + 1, z);
        var above = _n.GetBlock(x, y + 1, z);

        // Surface height at the four corners (index: i + 2 * j for corner (i, j)).
        Span<float> top = stackalloc float[4];
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
                top[i + 2 * j] = surface ? CornerHeight(x, y, z, i, j) : 1f;

        var flow = FlowDirection(chunk, x, y, z);
        float surfaceMask = surface ? 1f : 0f;
        var origin = new Vector3(chunk.WorldX + x, y, chunk.WorldZ + z);

        // Top: only where the water meets air (or something see-through), never under a roof of stone.
        if (surface && !BlockRegistry.IsOpaque(above))
        {
            _n.GetCell(x, y + 1, z, out _, out int sky, out int blk);
            var light = new Color(sky / 15f, blk / 15f, 1f, 0f);
            AddWaterQuad(light, new Vector3(0, 1, 0), surfaceMask, flow,
                origin + new Vector3(0, top[0], 0), origin + new Vector3(1, top[1], 0),
                origin + new Vector3(1, top[3], 1), origin + new Vector3(0, top[2], 1));
        }

        // Walls: only where the neighbour is see-through and not itself water (water-to-water needs no wall).
        for (int d = 0; d < 4; d++)
        {
            int dx = d == 0 ? 1 : d == 1 ? -1 : 0, dz = d == 2 ? 1 : d == 3 ? -1 : 0;
            var neighbour = _n.GetBlock(x + dx, y, z + dz);
            if (BlockRegistry.IsOpaque(neighbour) || BlockRegistry.IsLiquid(neighbour)) continue;

            _n.GetCell(x + dx, y, z + dz, out _, out int sky, out int blk);
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
        var below = _n.GetBlock(x, y - 1, z);
        if (!BlockRegistry.IsOpaque(below) && !BlockRegistry.IsLiquid(below))
        {
            _n.GetCell(x, y - 1, z, out _, out int sky, out int blk);
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
    private void AddWaterQuad(Color light, Vector3 normal, float surfaceMask, Vector2 flow,
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, (int, int)? topIndices = null)
    {
        int start = _waterVertices.Count;
        for (int i = 0; i < 4; i++)
        {
            bool isTop = topIndices is null || topIndices.Value.Item1 == i || topIndices.Value.Item2 == i;
            var extra = new Color(isTop ? surfaceMask : 0f, flow.X * 0.5f + 0.5f, flow.Y * 0.5f + 0.5f, 0f);
            var position = i == 0 ? p0 : i == 1 ? p1 : i == 2 ? p2 : p3;
            _waterVertices.Add(new VoxelVertex(position, normal, Vector2.Zero, light, extra));
        }
        _waterIndices.Add(start); _waterIndices.Add(start + 1); _waterIndices.Add(start + 2);
        _waterIndices.Add(start); _waterIndices.Add(start + 2); _waterIndices.Add(start + 3);
    }

    /// <summary>
    /// Which way the water moves, as a unit vector (zero if still). Rivers carry their direction from the generator;
    /// flowing water runs from low levels (near the source) toward high ones.
    /// </summary>
    private Vector2 FlowDirection(Chunk chunk, int x, int y, int z)
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
            if (!IsWater(x + dx, y, z + dz)) continue;
            int diff = _n.GetFluidLevel(x + dx, y, z + dz) - level;
            sum += new Vector2(dx, dz) * diff;
        }
        return sum.LengthSquared() < 1e-4f ? Vector2.Zero : Vector2.Normalize(sum);
    }
}
