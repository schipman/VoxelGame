using Microsoft.Xna.Framework;
using VoxelGame.Items;

namespace VoxelGame.UI;

/// <summary>What is being dragged out of an item slot: a stack, and the slot it came from.</summary>
public sealed class ItemDragPayload : DragPayload
{
    public ItemStack Stack { get; init; }

    public override void Draw(UiRenderer r, Point mouse)
    {
        var icon = new Rectangle(mouse.X - 8, mouse.Y - 8, 16, 16);
        r.Icon(Stack.Block, icon);
        ItemSlot.DrawCount(r, icon, Stack.Count);
    }
}

/// <summary>
/// One square that can hold a stack of items. Reads and writes the stack through two functions, so the same widget works
/// for inventory slots, hotbar slots and anything else that holds items.
///
/// Drag from a slot to move its stack: left button moves everything, right button moves half. Dropping on an empty slot
/// puts it there, on the same item merges, on a different item swaps. A slot marked <see cref="Infinite"/> is a source
/// that never runs out (the creative palette): dragging from it copies, and dropping onto it deletes.
/// </summary>
public sealed class ItemSlot : Widget
{
    public const int SlotSize = 18;

    private readonly Func<ItemStack> _get;
    private readonly Action<ItemStack> _set;

    /// <summary>A source that never runs out; also deletes whatever is dropped on it.</summary>
    public bool Infinite;

    /// <summary>False for slots that are just shown (the HUD hotbar): they ignore the mouse.</summary>
    public bool Interactive = true;

    /// <summary>Draws the gold frame (the selected hotbar slot).</summary>
    public bool Highlighted;

    /// <summary>Called when the slot is clicked without dragging.</summary>
    public Action<ItemSlot, ClickInfo> Clicked;

    public ItemSlot(Func<ItemStack> get, Action<ItemStack> set)
    {
        _get = get;
        _set = set;
        Size = new Point(SlotSize, SlotSize);
    }

    public ItemStack Stack => _get();

    public override bool HitTestVisible => Interactive;

    public override string TooltipText => Stack.IsEmpty ? null : Stack.Name;

    public override void OnClick(ClickInfo click) => Clicked?.Invoke(this, click);

    // ---- Drag and drop ----

    public override DragPayload BeginDrag(ClickInfo click)
    {
        var stack = Stack;
        if (!Interactive || stack.IsEmpty) return null;

        int amount = click.Button == MouseButton.Left
            ? (Infinite ? ItemStack.MaxCount : stack.Count)
            : (Infinite ? 1 : (stack.Count + 1) / 2);
        return new ItemDragPayload { Source = this, Stack = new ItemStack(stack.Block, amount) };
    }

    public override bool CanAcceptDrop(DragPayload payload) => Interactive && payload is ItemDragPayload;

    public override void OnDrop(DragPayload payload)
    {
        if (payload is not ItemDragPayload drag || drag.Source is not ItemSlot source || source == this) return;

        if (Infinite)
        {
            // Dropping on the palette throws the items away.
            if (!source.Infinite)
            {
                var from = source.Stack;
                from.Count -= drag.Stack.Count;
                source._set(from.Count > 0 ? from : ItemStack.Empty);
            }
        }
        else if (source.Infinite)
        {
            // Pulling from the palette: top up the same item, otherwise replace what was there.
            var to = Stack;
            if (!to.IsEmpty && to.Block == drag.Stack.Block)
                to.Count = Math.Min(ItemStack.MaxCount, to.Count + drag.Stack.Count);
            else
                to = drag.Stack;
            _set(to);
        }
        else
        {
            var from = source.Stack;
            var to = Stack;
            if (ItemStack.Transfer(ref from, ref to, drag.Stack.Count))
            {
                source._set(from);
                _set(to);
            }
        }
    }

    // ---- Drawing ----

    protected override void DrawSelf(UiRenderer r)
    {
        var area = Bounds;
        r.Fill(area, UiTheme.SlotFill);
        r.Bevel(area, UiTheme.PanelDark, UiTheme.PanelLight);   // sunken

        var stack = Stack;
        if (!stack.IsEmpty)
        {
            var icon = new Rectangle(area.X + 1, area.Y + 1, 16, 16);
            r.Icon(stack.Block, icon, IsDragSource ? 0.35f : 1f);
            if (!Infinite) DrawCount(r, icon, stack.Count);
        }

        if (IsDropTarget) r.Fill(area, UiTheme.DropTarget);
        else if (Interactive && IsHovered) r.Fill(new Rectangle(area.X + 1, area.Y + 1, area.Width - 2, area.Height - 2), UiTheme.SlotHover);

        if (Highlighted) r.Outline(new Rectangle(area.X - 1, area.Y - 1, area.Width + 2, area.Height + 2), UiTheme.SlotSelected, 1);
    }

    /// <summary>The stack size in the bottom-right corner of an icon (nothing for a single item).</summary>
    public static void DrawCount(UiRenderer r, Rectangle icon, int count)
    {
        if (count <= 1) return;
        string text = count.ToString();
        r.Text(text, new Point(icon.Right - r.TextWidth(text), icon.Bottom - PixelFont.CellSize + 1), UiTheme.Text);
    }
}
