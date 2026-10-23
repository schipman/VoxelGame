using Microsoft.Xna.Framework;

namespace VoxelGame.UI;

/// <summary>Text on screen. Supports several lines (\n). Sizes itself to its text unless you set Size yourself.</summary>
public sealed class Label : Widget
{
    private string _text = "";

    private int _scale = 1;

    public Color Color = UiTheme.Text;
    public bool Shadow = true;

    /// <summary>0 to 1; fade a label out by lowering it.</summary>
    public float Opacity = 1f;

    public override bool HitTestVisible => false;

    public Label(string text = "") => Text = text;

    /// <summary>Whole-number size multiplier (2 = double size).</summary>
    public int Scale
    {
        get => _scale;
        set { _scale = Math.Max(1, value); Remeasure(); }
    }

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; Remeasure(); }
    }

    private void Remeasure()
    {
        var font = PixelFont.Current;
        if (font != null) Size = new Point(font.Measure(_text, _scale), font.MeasureHeight(_text, _scale));
    }

    protected override void DrawSelf(UiRenderer renderer) =>
        renderer.Text(_text, AbsolutePosition, Color * Opacity, Scale, Shadow);
}

/// <summary>A clickable button with text.</summary>
public sealed class Button : Widget
{
    public string Text;
    public Action Clicked;

    public Button(string text, Action clicked = null, int width = 150, int height = 20)
    {
        Text = text;
        Clicked = clicked;
        Size = new Point(width, height);
    }

    public override void OnClick(ClickInfo click)
    {
        if (Enabled && click.Button == MouseButton.Left) Clicked?.Invoke();
    }

    protected override void DrawSelf(UiRenderer r)
    {
        var area = Bounds;
        var fill = !Enabled ? UiTheme.ButtonDisabled : IsPressed ? UiTheme.ButtonPressed : IsHovered ? UiTheme.ButtonHover : UiTheme.ButtonFill;
        r.Fill(area, fill);
        r.Outline(area, UiTheme.PanelBorder);

        var inner = new Rectangle(area.X + 1, area.Y + 1, area.Width - 2, area.Height - 2);
        if (Enabled && !IsPressed) r.Bevel(inner, UiTheme.PanelLight, UiTheme.PanelDark);
        else r.Bevel(inner, UiTheme.PanelDark, UiTheme.PanelLight);

        r.TextCentered(Text, new Rectangle(area.X, area.Y + (IsPressed ? 1 : 0), area.Width, area.Height), Enabled ? UiTheme.Text : UiTheme.TextDim);
    }
}

/// <summary>A button that steps through a list of options when clicked (right-click goes back): "Game mode: Creative".</summary>
public sealed class CycleButton : Widget
{
    private readonly string _label;
    private readonly string[] _options;

    public int Index;
    public Action<int> Changed;

    public CycleButton(string label, string[] options, int index, Action<int> changed, int width = 200, int height = 20)
    {
        _label = label;
        _options = options;
        Index = Math.Clamp(index, 0, options.Length - 1);
        Changed = changed;
        Size = new Point(width, height);
    }

    public override void OnClick(ClickInfo click)
    {
        if (!Enabled) return;
        int step = click.Button == MouseButton.Right ? -1 : 1;
        Index = (Index + step + _options.Length) % _options.Length;
        Changed?.Invoke(Index);
    }

    protected override void DrawSelf(UiRenderer r)
    {
        var area = Bounds;
        r.Fill(area, IsPressed ? UiTheme.ButtonPressed : IsHovered ? UiTheme.ButtonHover : UiTheme.ButtonFill);
        r.Outline(area, UiTheme.PanelBorder);
        r.Bevel(new Rectangle(area.X + 1, area.Y + 1, area.Width - 2, area.Height - 2), UiTheme.PanelLight, UiTheme.PanelDark);
        r.TextCentered($"{_label}: {_options[Index]}", area, UiTheme.Text);
    }
}

/// <summary>A horizontal slider over a whole-number range. Click or drag along it.</summary>
public sealed class Slider : Widget
{
    private readonly string _label;
    private readonly Func<int, string> _format;
    private int _value;

    public int Min, Max;
    public Action<int> Changed;

    public Slider(string label, int min, int max, int value, Action<int> changed, Func<int, string> format = null, int width = 200, int height = 20)
    {
        _label = label;
        Min = min;
        Max = max;
        _value = Math.Clamp(value, min, max);
        Changed = changed;
        _format = format ?? (v => v.ToString());
        Size = new Point(width, height);
    }

    public int Value
    {
        get => _value;
        set => _value = Math.Clamp(value, Min, Max);
    }

    public override void OnPress(ClickInfo click) => Drag(click);
    public override void OnHeld(ClickInfo click) => Drag(click);

    private void Drag(ClickInfo click)
    {
        if (!Enabled || click.Button != MouseButton.Left) return;

        var area = Bounds;
        float t = Math.Clamp((click.Position.X - (area.X + 3)) / (float)(area.Width - 6), 0f, 1f);
        int value = Min + (int)MathF.Round(t * (Max - Min));
        if (value == _value) return;
        _value = value;
        Changed?.Invoke(_value);
    }

    protected override void DrawSelf(UiRenderer r)
    {
        var area = Bounds;
        r.Fill(area, UiTheme.SliderTrack);
        r.Outline(area, UiTheme.PanelBorder);

        // The filled part and the handle.
        float t = Max == Min ? 0f : (_value - Min) / (float)(Max - Min);
        int handleX = area.X + 3 + (int)(t * (area.Width - 6)) - 2;
        r.Fill(new Rectangle(area.X + 1, area.Y + 1, handleX - area.X, area.Height - 2), UiTheme.SliderFill * 0.55f);

        var handle = new Rectangle(handleX, area.Y + 1, 5, area.Height - 2);
        r.Fill(handle, IsHovered || IsPressed ? UiTheme.ButtonHover : UiTheme.ButtonFill);
        r.Bevel(handle, UiTheme.PanelLight, UiTheme.PanelDark);

        r.TextCentered($"{_label}: {_format(_value)}", area, UiTheme.Text);
    }
}
