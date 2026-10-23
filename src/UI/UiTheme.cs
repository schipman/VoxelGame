using Microsoft.Xna.Framework;

namespace VoxelGame.UI;

/// <summary>Every colour the UI uses, in one place. Change these to restyle the whole interface.</summary>
public static class UiTheme
{
    /// <summary>
    /// A colour with transparency. Sprite drawing expects "premultiplied" colours (red, green and blue already scaled by
    /// alpha); writing <c>new Color(255, 255, 255, 54)</c> directly would add a full-strength white instead of a faint one.
    /// Always build see-through colours with this.
    /// </summary>
    public static Color Alpha(int r, int g, int b, int a) => Color.FromNonPremultiplied(r, g, b, a);

    // Text
    public static readonly Color Text = new(236, 238, 244);
    public static readonly Color TextDim = new(150, 154, 168);
    public static readonly Color TextShadow = Alpha(0, 0, 0, 170);
    public static readonly Color Accent = new(255, 214, 110);

    // Windows
    public static readonly Color ScreenDim = Alpha(0, 0, 0, 120);
    public static readonly Color PanelFill = Alpha(44, 46, 56, 244);
    public static readonly Color PanelBorder = new(14, 15, 20);
    public static readonly Color PanelLight = new(92, 96, 112);
    public static readonly Color PanelDark = new(26, 27, 34);

    // Buttons
    public static readonly Color ButtonFill = new(72, 76, 94);
    public static readonly Color ButtonHover = new(98, 104, 132);
    public static readonly Color ButtonPressed = new(52, 55, 70);
    public static readonly Color ButtonDisabled = new(50, 51, 58);

    // Item slots
    public static readonly Color SlotFill = new(28, 29, 36);
    public static readonly Color SlotHover = Alpha(255, 255, 255, 54);
    public static readonly Color SlotSelected = new(255, 214, 110);
    public static readonly Color DropTarget = Alpha(120, 220, 140, 90);

    // Sliders
    public static readonly Color SliderTrack = new(22, 23, 29);
    public static readonly Color SliderFill = new(86, 140, 220);
}
