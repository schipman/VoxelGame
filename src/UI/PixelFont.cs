using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Rendering;

namespace VoxelGame.UI;

/// <summary>
/// The UI font: a bitmap font read from Assets/Textures/font.png, so it can be restyled in any image editor
/// (press F5 in game to reload). The image is a 16x6 grid of 8x8 cells holding ASCII 32 to 127, white on transparent;
/// the last cell (127) is drawn for characters the font doesn't have. Each character's width is measured from its
/// pixels, so narrow letters like "i" take less room than "m".
/// </summary>
public sealed class PixelFont : IDisposable
{
    public const int CellSize = 8;
    public const int LineHeight = 9;          // 8 pixel cell + 1 pixel gap, at scale 1
    private const int Columns = 16;
    private const int FirstChar = 32;
    private const int CharCount = 96;
    private const string Path = "Assets/Textures/font.png";

    private Texture2D _texture;
    private readonly int[] _advance = new int[CharCount];   // width of each glyph plus the gap after it

    /// <summary>The font widgets use when they need to measure text outside of drawing (e.g. to size a label).</summary>
    public static PixelFont Current { get; private set; }

    private PixelFont() { }

    public static PixelFont Load(GraphicsDevice device)
    {
        var font = new PixelFont();
        font.Reload(device);
        Current = font;
        return font;
    }

    /// <summary>Re-reads the image. If it is broken, the old one stays in use.</summary>
    public void Reload(GraphicsDevice device)
    {
        var texture = AssetFiles.LoadTexture(device, Path);
        if (texture.Width < Columns * CellSize || texture.Height < CharCount / Columns * CellSize)
        {
            texture.Dispose();
            throw new InvalidDataException($"{Path} must be at least {Columns * CellSize}x{CharCount / Columns * CellSize} pixels.");
        }

        var pixels = new Color[texture.Width * texture.Height];
        texture.GetData(pixels);
        for (int i = 0; i < CharCount; i++)
        {
            int ox = i % Columns * CellSize, oy = i / Columns * CellSize;
            int width = 0;
            for (int y = 0; y < CellSize; y++)
                for (int x = 0; x < CellSize; x++)
                    if (pixels[(oy + y) * texture.Width + ox + x].A > 0) width = Math.Max(width, x + 1);

            _advance[i] = width == 0 ? 4 : width + 1;   // a blank cell (space) is 3 wide plus the gap
        }

        _texture?.Dispose();
        _texture = texture;
    }

    private static int GlyphIndex(char c) => c >= FirstChar && c < FirstChar + CharCount - 1 ? c - FirstChar : CharCount - 1;

    /// <summary>Width in pixels of the widest line.</summary>
    public int Measure(string text, int scale = 1)
    {
        int widest = 0, line = 0;
        foreach (char c in text)
        {
            if (c == '\n') { widest = Math.Max(widest, line); line = 0; continue; }
            line += _advance[GlyphIndex(c)];
        }
        return Math.Max(widest, line) * scale - (text.Length > 0 ? scale : 0);   // no gap after the last letter
    }

    public int MeasureHeight(string text, int scale = 1)
    {
        int lines = 1;
        foreach (char c in text) if (c == '\n') lines++;
        return (lines * LineHeight - 1) * scale;
    }

    public void Draw(SpriteBatch batch, string text, Point position, Color color, int scale = 1)
    {
        int x = position.X, y = position.Y;
        foreach (char c in text)
        {
            if (c == '\n') { x = position.X; y += LineHeight * scale; continue; }

            int i = GlyphIndex(c);
            if (c != ' ')
            {
                var source = new Rectangle(i % Columns * CellSize, i / Columns * CellSize, CellSize, CellSize);
                batch.Draw(_texture, new Rectangle(x, y, CellSize * scale, CellSize * scale), source, color);
            }
            x += _advance[i] * scale;
        }
    }

    public void Dispose() => _texture?.Dispose();
}
