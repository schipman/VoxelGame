using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;

namespace VoxelGame.Rendering;

/// <summary>
/// Pictures of every block for the UI: a little isometric cube per block type, drawn once into a texture using the
/// real block textures. It is rebuilt when the block atlas is reloaded or the UI scale changes, so icons are always
/// drawn pixel for pixel at the size they are shown.
/// </summary>
public sealed class BlockIconAtlas : IDisposable
{
    private const int PerRow = 8;

    // Corner tables and UVs match ChunkMesher, so the textures sit the same way up as in the world.
    private static readonly Vector3[] Top  = { new(0, 1, 0), new(1, 1, 0), new(1, 1, 1), new(0, 1, 1) };
    private static readonly Vector3[] PosX = { new(1, 0, 0), new(1, 0, 1), new(1, 1, 1), new(1, 1, 0) };
    private static readonly Vector3[] PosZ = { new(0, 0, 1), new(0, 1, 1), new(1, 1, 1), new(1, 0, 1) };
    private static readonly Vector2[] UvTop  = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
    private static readonly Vector2[] UvSideA = { new(1, 1), new(0, 1), new(0, 0), new(1, 0) };
    private static readonly Vector2[] UvSideB = { new(0, 1), new(0, 0), new(1, 0), new(1, 1) };

    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    private readonly Dictionary<BlockType, Rectangle> _sources = new();
    private RenderTarget2D _target;

    public Texture2D Texture => _target;
    public int IconSize { get; private set; }

    public BlockIconAtlas(GraphicsDevice device)
    {
        _device = device;
        _effect = new BasicEffect(device)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            FogEnabled = false,
        };

        // Isometric view of a unit cube: from the (1,1,1) corner, so the top and the +X and +Z sides show.
        _effect.View = Matrix.CreateLookAt(new Vector3(4, 4, 4), Vector3.Zero, Vector3.Up);
        _effect.Projection = Matrix.CreateOrthographic(1.72f, 1.72f, 0.1f, 20f);
        _effect.World = Matrix.CreateTranslation(-0.5f, -0.5f, -0.5f);
    }

    public bool TryGetSource(BlockType block, out Rectangle source) => _sources.TryGetValue(block, out source);

    /// <summary>Draws all icons, <paramref name="iconSize"/> pixels square each, using the given block texture atlas.</summary>
    public void Rebuild(Texture2D blockAtlas, int iconSize)
    {
        var blocks = Enum.GetValues<BlockType>().Where(b => b != BlockType.Air).ToList();
        int rows = (blocks.Count + PerRow - 1) / PerRow;
        int width = PerRow * iconSize, height = rows * iconSize;

        if (_target == null || _target.Width != width || _target.Height != height)
        {
            _target?.Dispose();
            _target = new RenderTarget2D(_device, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        }
        IconSize = iconSize;
        _sources.Clear();

        _device.SetRenderTarget(_target);
        _device.Clear(Color.Transparent);
        _device.BlendState = BlendState.Opaque;
        _device.DepthStencilState = DepthStencilState.None;
        _device.RasterizerState = RasterizerState.CullNone;
        _device.SamplerStates[0] = SamplerState.PointClamp;
        _effect.Texture = blockAtlas;

        for (int i = 0; i < blocks.Count; i++)
        {
            var cell = new Rectangle(i % PerRow * iconSize, i / PerRow * iconSize, iconSize, iconSize);
            _sources[blocks[i]] = cell;
            _device.Viewport = new Viewport(cell.X, cell.Y, cell.Width, cell.Height);
            DrawCube(blocks[i]);
        }

        _device.SetRenderTarget(null);
    }

    private void DrawCube(BlockType block)
    {
        var def = BlockRegistry.Get(block);
        bool liquid = def.IsLiquid;   // water has no texture of its own: draw it as a tinted block
        _effect.TextureEnabled = !liquid;

        var vertices = new List<VertexPositionColorTexture>(12);
        var indices = new List<short>(18);
        void Face(Vector3[] corners, Vector2[] uvs, Tile tile, float shade)
        {
            var color = liquid ? new Color(0.22f * shade, 0.52f * shade, 0.9f * shade) : new Color(shade, shade, shade);
            short start = (short)vertices.Count;
            for (int i = 0; i < 4; i++)
                vertices.Add(new VertexPositionColorTexture(corners[i], color, TextureAtlas.MapUV(tile, uvs[i])));
            indices.AddRange(new short[] { start, (short)(start + 1), (short)(start + 2), start, (short)(start + 2), (short)(start + 3) });
        }

        Face(Top,  UvTop,   def.Top,  1.00f);
        Face(PosZ, UvSideB, def.Side, 0.78f);   // left face on screen
        Face(PosX, UvSideA, def.Side, 0.58f);   // right face on screen

        _effect.CurrentTechnique.Passes[0].Apply();
        _device.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, vertices.ToArray(), 0, vertices.Count, indices.ToArray(), 0, indices.Count / 3);
    }

    public void Dispose()
    {
        _target?.Dispose();
        _effect.Dispose();
    }
}
