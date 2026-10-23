using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Input;

namespace VoxelGame.UI;

/// <summary>
/// Owns the UI: the always-visible HUD, the stack of open screens, and all mouse handling.
///
/// Each frame, while a screen is open, it works out which widget is under the mouse and turns raw mouse state into
/// widget events: hover, press, held, click, scroll, and drag and drop. A press that moves more than a few pixels
/// asks the widget under it to start a drag (<see cref="Widget.BeginDrag"/>); releasing over a widget that accepts
/// the payload drops it there. Everything is laid out in "UI pixels" and scaled up to the window by a whole number,
/// so the UI stays crisp at any resolution.
/// </summary>
public sealed class UiManager
{
    private const int DragThreshold = 4;   // UI pixels the mouse must move with a button held before a drag starts

    private readonly SpriteBatch _batch;
    private readonly List<UiScreen> _screens = new();

    private Widget _hovered;
    private Widget _pressed;
    private MouseButton _pressedButton;
    private Point _pressPosition;
    private DragPayload _drag;
    private Widget _dropTarget;
    private Point _mouse;

    public UiRenderer Renderer { get; }

    /// <summary>The always-visible layer (hotbar, crosshair, debug text). Draws under any open screens.</summary>
    public Hud Hud { get; set; }

    /// <summary>Window size in UI pixels.</summary>
    public Point UiSize { get; private set; }

    /// <summary>Whole-number factor between UI pixels and screen pixels.</summary>
    public int Scale { get; private set; } = 1;

    /// <summary>Raised when the scale changes: rebuild anything drawn at a fixed pixel size (the block icons).</summary>
    public event Action ScaleChanged;

    public UiManager(SpriteBatch batch, UiRenderer renderer)
    {
        _batch = batch;
        Renderer = renderer;
    }

    // ---- Screens ----

    public bool HasScreen => _screens.Count > 0;
    public UiScreen Top => _screens.Count > 0 ? _screens[^1] : null;
    public bool PausesGame => _screens.Any(s => s.PausesGame);

    public void Open(UiScreen screen)
    {
        screen.Attach(this);
        _screens.Add(screen);
        ResetMouseState();
        screen.OnOpened();
    }

    /// <summary>Closes the top screen.</summary>
    public void CloseTop()
    {
        if (_screens.Count == 0) return;
        var screen = _screens[^1];
        _screens.RemoveAt(_screens.Count - 1);
        ResetMouseState();
        screen.OnClosed();
    }

    public void CloseAll()
    {
        while (_screens.Count > 0) CloseTop();
    }

    /// <summary>Escape: closes the top screen if it allows it. Returns whether anything was closed.</summary>
    public bool HandleEscape()
    {
        if (Top is not { ClosesOnEscape: true }) return false;
        CloseTop();
        return true;
    }

    public void ShowMessage(string text, float seconds = 2.5f) => Hud?.ShowMessage(text, seconds);

    // ---- Per-frame ----

    public void Update(InputState input, float dt, Point windowSize, int scaleSetting)
    {
        // Automatic scale: about one UI pixel for every 1/360 of the window height (720p: 2, 1080p: 3, 1440p: 4).
        int scale = scaleSetting > 0 ? scaleSetting : Math.Max(1, (windowSize.Y + 60) / 360);
        bool scaleChanged = scale != Scale;
        Scale = scale;
        UiSize = new Point(Math.Max(windowSize.X / scale, 1), Math.Max(windowSize.Y / scale, 1));
        if (scaleChanged) ScaleChanged?.Invoke();

        if (Hud != null)
        {
            Hud.Root.Size = UiSize;
            Hud.ScreenOpen = HasScreen;
            Hud.Root.Layout();
            Hud.Update(dt);
            Hud.Root.Update(dt);
        }

        foreach (var screen in _screens)
        {
            screen.Root.Size = UiSize;
            screen.Root.Layout();
            screen.Root.Update(dt);
        }

        if (Top == null) return;
        _mouse = new Point(input.MousePosition.X / scale, input.MousePosition.Y / scale);
        RouteMouse(input);
    }

    private void RouteMouse(InputState input)
    {
        var root = Top.Root;
        var hit = root.HitTest(_mouse);

        if (hit != _hovered)
        {
            if (_hovered != null) { _hovered.IsHovered = false; _hovered.OnMouseLeave(); }
            _hovered = hit;
            if (_hovered != null) { _hovered.IsHovered = true; _hovered.OnMouseEnter(); }
        }

        // A press starts tracking a widget.
        if (_pressed == null && hit != null)
        {
            MouseButton? pressedNow = input.LeftClicked ? MouseButton.Left : input.RightClicked ? MouseButton.Right : null;
            if (pressedNow is { } button && hit.Enabled)
            {
                _pressed = hit;
                _pressedButton = button;
                _pressPosition = _mouse;
                _pressed.IsPressed = true;
                _pressed.OnPress(new ClickInfo(button, _mouse, input.ShiftHeld));
            }
        }

        if (_pressed != null)
        {
            bool held = _pressedButton == MouseButton.Left ? input.LeftHeld : input.RightHeld;
            var info = new ClickInfo(_pressedButton, _mouse, input.ShiftHeld);

            if (held)
            {
                _pressed.OnHeld(info);
                if (_drag == null && Distance(_mouse, _pressPosition) >= DragThreshold)
                {
                    _drag = _pressed.BeginDrag(info);
                    if (_drag != null) _pressed.IsDragSource = true;
                }
                if (_drag != null) UpdateDropTarget(hit);
            }
            else
            {
                Release(hit, info);
            }
        }

        // Scroll goes to the widget under the mouse, then up through its parents until one handles it.
        if (input.ScrollDelta != 0)
            for (var w = hit; w != null; w = w.Parent)
                if (w.OnScroll(input.ScrollDelta)) break;
    }

    private void UpdateDropTarget(Widget hit)
    {
        Widget target = null;
        for (var w = hit; w != null; w = w.Parent)
            if (w.Enabled && w.CanAcceptDrop(_drag)) { target = w; break; }

        if (target == _dropTarget) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = target;
        if (_dropTarget != null) _dropTarget.IsDropTarget = true;
    }

    private void Release(Widget hit, ClickInfo info)
    {
        var pressed = _pressed;
        pressed.IsPressed = false;

        if (_drag != null)
        {
            var target = _dropTarget;
            target?.OnDrop(_drag);
            pressed.OnDragEnded(_drag, target != null);
            pressed.IsDragSource = false;
            if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        }
        else if (hit == pressed)
        {
            pressed.OnClick(info);
        }

        _pressed = null;
        _drag = null;
        _dropTarget = null;
    }

    /// <summary>Forget what the mouse was doing (when a screen opens or closes, widgets may have disappeared).</summary>
    private void ResetMouseState()
    {
        if (_hovered != null) _hovered.IsHovered = false;
        if (_pressed != null) { _pressed.IsPressed = false; _pressed.IsDragSource = false; }
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _hovered = _pressed = _dropTarget = null;
        _drag = null;
    }

    private static float Distance(Point a, Point b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---- Drawing ----

    /// <summary>Draws the HUD and open screens over whatever is already in the back buffer.</summary>
    public void Draw()
    {
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone,
            transformMatrix: Matrix.CreateScale(Scale, Scale, 1f));

        Hud?.Root.Draw(Renderer);

        foreach (var screen in _screens)
        {
            if (screen.DimBackground) Renderer.Fill(new Rectangle(Point.Zero, UiSize), UiTheme.ScreenDim);
            screen.Root.Draw(Renderer);
        }

        if (HasScreen)
        {
            _drag?.Draw(Renderer, _mouse);
            if (_drag == null) DrawTooltip();
        }

        _batch.End();
    }

    private void DrawTooltip()
    {
        string text = _hovered?.TooltipText;
        if (string.IsNullOrEmpty(text)) return;

        var size = new Point(Renderer.TextWidth(text) + 8, Renderer.TextHeight(text) + 6);
        var position = new Point(
            Math.Min(_mouse.X + 10, UiSize.X - size.X - 1),
            Math.Min(_mouse.Y + 10, UiSize.Y - size.Y - 1));
        var area = new Rectangle(position, size);

        Renderer.Window(area);
        Renderer.Text(text, new Point(area.X + 4, area.Y + 3), UiTheme.Text);
    }
}
