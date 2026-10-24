using Microsoft.Xna.Framework;

namespace VoxelGame.UI;

/// <summary>
/// A single line of editable text. Click it to type; the game forwards typed characters to <see cref="Focused"/> (see
/// VoxelGameApp, which listens to the window's text input).
/// </summary>
public sealed class TextBox : Widget
{
    /// <summary>The box that receives typing, or null. Cleared when a screen closes (see <see cref="ReleaseFocus"/>).</summary>
    public static TextBox Focused { get; private set; }

    private float _blink;

    public string Text = "";
    public string Placeholder = "";
    public int MaxLength = 32;
    public Func<char, bool> AllowedChar = c => !char.IsControl(c);
    public Action Submitted;

    public TextBox(int width = 200, int height = 20)
    {
        Size = new Point(width, height);
    }

    public void Focus() { Focused = this; _blink = 0; }
    public static void ReleaseFocus() => Focused = null;

    public override void OnClick(ClickInfo click) => Focus();

    /// <summary>A character was typed (backspace is \b, Enter is \r).</summary>
    public void Type(char c)
    {
        _blink = 0;
        if (c == '\b') { if (Text.Length > 0) Text = Text[..^1]; }
        else if (c == '\r' || c == '\n') Submitted?.Invoke();
        else if (Text.Length < MaxLength && AllowedChar(c)) Text += c;
    }

    public override void Update(float dt)
    {
        _blink += dt;
        base.Update(dt);
    }

    protected override void DrawSelf(UiRenderer r)
    {
        var area = Bounds;
        bool focused = Focused == this;
        r.Fill(area, UiTheme.SlotFill);
        r.Outline(area, focused ? UiTheme.Accent : UiTheme.PanelBorder);

        int y = area.Y + (area.Height - r.TextHeight("A")) / 2;
        if (Text.Length == 0 && !focused)
        {
            r.Text(Placeholder, new Point(area.X + 4, y), UiTheme.TextDim, 1, false);
            return;
        }

        // Show the end of long text, so the caret stays in view.
        string shown = Text;
        while (shown.Length > 1 && r.TextWidth(shown) > area.Width - 10) shown = shown[1..];
        r.Text(shown, new Point(area.X + 4, y), UiTheme.Text, 1, false);

        if (focused && (int)(_blink * 2) % 2 == 0)
            r.Fill(new Rectangle(area.X + 4 + r.TextWidth(shown), area.Y + 3, 1, area.Height - 6), UiTheme.Text);
    }
}
