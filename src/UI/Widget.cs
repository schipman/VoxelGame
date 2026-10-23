using Microsoft.Xna.Framework;

namespace VoxelGame.UI;

public enum MouseButton { Left, Right }

/// <summary>A mouse press or click: which button, where (in UI pixels), and whether Shift was held.</summary>
public readonly record struct ClickInfo(MouseButton Button, Point Position, bool Shift);

/// <summary>Where a widget sits inside its parent. None means "use Position"; the rest pin it to an edge or the middle.</summary>
public enum Anchor { None, TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

/// <summary>
/// Something being dragged with the mouse. Subclass it to carry your own data (see ItemDragPayload) and to draw the
/// thing that follows the cursor.
/// </summary>
public abstract class DragPayload
{
    /// <summary>The widget the drag started on.</summary>
    public Widget Source { get; init; }

    /// <summary>Draws the dragged thing at the mouse position.</summary>
    public abstract void Draw(UiRenderer renderer, Point mouse);
}

/// <summary>
/// Base class of everything on screen. Widgets form a tree: a widget's position is relative to its parent, and it is
/// drawn and hit-tested on top of its parent and before its later siblings.
///
/// To make a new widget, subclass this and override what you need:
///   DrawSelf        how it looks
///   OnClick, ...    how it reacts to the mouse (the UiManager calls these)
///   BeginDrag etc.  drag and drop
/// </summary>
public abstract class Widget
{
    private readonly List<Widget> _children = new();

    public Widget Parent { get; private set; }
    public IReadOnlyList<Widget> Children => _children;

    /// <summary>Top-left corner relative to the parent. Only used when Anchor is None.</summary>
    public Point Position;
    public Point Size;

    /// <summary>Pins the widget to part of its parent; Offset then nudges it from there.</summary>
    public Anchor Anchor;
    public Point Offset;

    public bool Visible = true;

    /// <summary>A disabled widget is drawn dimmed and ignores clicks, but still blocks the mouse.</summary>
    public bool Enabled = true;

    /// <summary>Set by the UiManager.</summary>
    public bool IsHovered { get; internal set; }
    public bool IsPressed { get; internal set; }
    public bool IsDragSource { get; internal set; }
    public bool IsDropTarget { get; internal set; }

    /// <summary>False for widgets the mouse should pass through (labels, decoration).</summary>
    public virtual bool HitTestVisible => true;

    /// <summary>Text shown in a small box when the mouse rests on the widget. Null or empty for none.</summary>
    public virtual string TooltipText => null;

    // ---- Tree ----

    public T Add<T>(T child) where T : Widget
    {
        child.Parent?._children.Remove(child);
        child.Parent = this;
        _children.Add(child);
        return child;
    }

    public void Remove(Widget child)
    {
        if (_children.Remove(child)) child.Parent = null;
    }

    public void Clear()
    {
        foreach (var child in _children) child.Parent = null;
        _children.Clear();
    }

    // ---- Position ----

    public Point LocalPosition
    {
        get
        {
            if (Anchor == Anchor.None || Parent == null) return Position;

            int x = Anchor switch
            {
                Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => 0,
                Anchor.TopRight or Anchor.Right or Anchor.BottomRight => Parent.Size.X - Size.X,
                _ => (Parent.Size.X - Size.X) / 2,
            };
            int y = Anchor switch
            {
                Anchor.TopLeft or Anchor.Top or Anchor.TopRight => 0,
                Anchor.BottomLeft or Anchor.Bottom or Anchor.BottomRight => Parent.Size.Y - Size.Y,
                _ => (Parent.Size.Y - Size.Y) / 2,
            };
            return new Point(x + Offset.X, y + Offset.Y);
        }
    }

    public Point AbsolutePosition => Parent == null ? LocalPosition : Parent.AbsolutePosition + LocalPosition;

    public Rectangle Bounds => new(AbsolutePosition, Size);

    // ---- Per-frame ----

    /// <summary>Sizes and places children. Children are laid out first, so a parent can size itself to fit them.</summary>
    public void Layout()
    {
        foreach (var child in _children) child.Layout();
        Arrange();
    }

    protected virtual void Arrange() { }

    public virtual void Update(float dt)
    {
        foreach (var child in _children) child.Update(dt);
    }

    public void Draw(UiRenderer renderer)
    {
        if (!Visible) return;
        DrawSelf(renderer);
        foreach (var child in _children) child.Draw(renderer);
    }

    protected virtual void DrawSelf(UiRenderer renderer) { }

    /// <summary>The topmost visible widget at a point (children before parents, later siblings before earlier ones), or null.</summary>
    public Widget HitTest(Point point)
    {
        if (!Visible || !Bounds.Contains(point)) return null;
        for (int i = _children.Count - 1; i >= 0; i--)
        {
            var hit = _children[i].HitTest(point);
            if (hit != null) return hit;
        }
        return HitTestVisible ? this : null;
    }

    // ---- Mouse (called by the UiManager) ----

    public virtual void OnMouseEnter() { }
    public virtual void OnMouseLeave() { }

    /// <summary>A button went down on this widget.</summary>
    public virtual void OnPress(ClickInfo click) { }

    /// <summary>Called every frame while a button that went down on this widget is still held (sliders use this).</summary>
    public virtual void OnHeld(ClickInfo click) { }

    /// <summary>A press and release on the same widget, without a drag in between.</summary>
    public virtual void OnClick(ClickInfo click) { }

    /// <summary>Scrolled over this widget. Return true if handled; otherwise the parent gets a go.</summary>
    public virtual bool OnScroll(int delta) => false;

    // ---- Drag and drop ----

    /// <summary>
    /// The mouse moved a few pixels with a button held down on this widget. Return what is being dragged, or null to not drag.
    /// </summary>
    public virtual DragPayload BeginDrag(ClickInfo click) => null;

    /// <summary>While dragging, whether this widget would take the dropped thing (used to highlight drop targets).</summary>
    public virtual bool CanAcceptDrop(DragPayload payload) => false;

    /// <summary>The button was released over this widget while dragging, and CanAcceptDrop said yes.</summary>
    public virtual void OnDrop(DragPayload payload) { }

    /// <summary>On the widget the drag started from, once the button is released: accepted is false if it was dropped on nothing.</summary>
    public virtual void OnDragEnded(DragPayload payload, bool accepted) { }
}

/// <summary>An invisible full-size widget that a screen's contents are attached to.</summary>
public sealed class ScreenRoot : Widget
{
    public override bool HitTestVisible => false;
}
