using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;
using VoxelGame.Rendering;

namespace VoxelGame.UI;

/// <summary>
/// What widgets draw with. All coordinates are in UI pixels; the UI manager scales everything up to the real screen
/// with a transform, so a widget never needs to know the window size or UI scale.
/// </summary>
public sealed class UiRenderer
{
    private readonly SpriteBatch _batch;
    private readonly Texture2D _pixel;

    public PixelFont Font { get; }
    public BlockIconAtlas Icons { get; }

    public UiRenderer(SpriteBatch batch, Texture2D pixel, PixelFont font, BlockIconAtlas icons)
    {
        _batch = batch;
        _pixel = pixel;
        Font = font;
        Icons = icons;
    }

    // ---- Shapes ----

    public void Fill(Rectangle area, Color color) => _batch.Draw(_pixel, area, color);

    public void Outline(Rectangle area, Color color, int thickness = 1)
    {
        Fill(new Rectangle(area.Left, area.Top, area.Width, thickness), color);
        Fill(new Rectangle(area.Left, area.Bottom - thickness, area.Width, thickness), color);
        Fill(new Rectangle(area.Left, area.Top + thickness, thickness, area.Height - 2 * thickness), color);
        Fill(new Rectangle(area.Right - thickness, area.Top + thickness, thickness, area.Height - 2 * thickness), color);
    }

    /// <summary>A one-pixel highlight on the top and left edges and a shadow on the bottom and right: raised, or sunken if swapped.</summary>
    public void Bevel(Rectangle area, Color light, Color dark)
    {
        Fill(new Rectangle(area.Left, area.Top, area.Width - 1, 1), light);
        Fill(new Rectangle(area.Left, area.Top, 1, area.Height - 1), light);
        Fill(new Rectangle(area.Left + 1, area.Bottom - 1, area.Width - 1, 1), dark);
        Fill(new Rectangle(area.Right - 1, area.Top + 1, 1, area.Height - 1), dark);
    }

    /// <summary>A window background: dark outline, flat fill and a raised bevel.</summary>
    public void Window(Rectangle area)
    {
        Fill(area, UiTheme.PanelFill);
        Outline(area, UiTheme.PanelBorder);
        Bevel(new Rectangle(area.X + 1, area.Y + 1, area.Width - 2, area.Height - 2), UiTheme.PanelLight, UiTheme.PanelDark);
    }

    // ---- Text ----

    public int TextWidth(string text, int scale = 1) => Font.Measure(text, scale);
    public int TextHeight(string text, int scale = 1) => Font.MeasureHeight(text, scale);

    public void Text(string text, Point position, Color color, int scale = 1, bool shadow = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (shadow)
        {
            var shadowColor = UiTheme.TextShadow * (color.A / 255f);
            Font.Draw(_batch, text, new Point(position.X + scale, position.Y + scale), shadowColor, scale);
        }
        Font.Draw(_batch, text, position, color, scale);
    }

    /// <summary>Text centred in a rectangle.</summary>
    public void TextCentered(string text, Rectangle area, Color color, int scale = 1, bool shadow = true)
    {
        var size = new Point(TextWidth(text, scale), TextHeight(text, scale));
        Text(text, new Point(area.X + (area.Width - size.X) / 2, area.Y + (area.Height - size.Y) / 2), color, scale, shadow);
    }

    // ---- Items ----

    /// <summary>Draws the picture of a block into a rectangle.</summary>
    public void Icon(BlockType block, Rectangle area, float opacity = 1f)
    {
        if (Icons.TryGetSource(block, out var source))
            _batch.Draw(Icons.Texture, area, source, Color.White * opacity);
    }
}
