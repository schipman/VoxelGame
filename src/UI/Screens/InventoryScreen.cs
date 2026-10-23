using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Items;
using VoxelGame.Settings;

namespace VoxelGame.UI.Screens;

/// <summary>
/// The inventory: the three rows of storage and the hotbar, all draggable. In creative mode a palette of every block
/// sits above it: drag from there to fill slots, drop items on it to delete them, or click to add a stack.
/// Other ways to move things: shift-click sends a stack between the hotbar and the rest; right-drag moves half.
/// </summary>
public sealed class InventoryScreen : UiScreen
{
    private const int PaletteRows = 4;
    private const int PaletteSize = Inventory.Columns * PaletteRows;

    private readonly Inventory _inventory;
    private readonly BlockType[] _palette;
    private int _page;
    private Label _pageLabel;

    // Opening it again with E closes it.
    public override bool PausesGame => false;

    public InventoryScreen(Inventory inventory, GameSettings settings)
    {
        _inventory = inventory;
        _palette = Enum.GetValues<BlockType>().Where(b => b != BlockType.Air).ToArray();

        var column = Root.Add(new StackPanel { Spacing = 6, CenterChildren = true, Anchor = Anchor.Center });
        if (settings.GameMode == GameMode.Creative) column.Add(BuildPalette());
        column.Add(BuildInventory());
    }

    // ---- The inventory itself ----

    private Widget BuildInventory()
    {
        var window = new StackPanel { Background = true, Padding = 8, Spacing = 5 };
        window.Add(new Label("Inventory") { Color = UiTheme.Accent });

        var main = window.Add(NewGrid());
        for (int i = Inventory.HotbarSize; i < Inventory.Size; i++) main.Add(MakeSlot(i));

        window.Add(new Label("Hotbar") { Color = UiTheme.TextDim });
        var hotbar = window.Add(NewGrid());
        for (int i = 0; i < Inventory.HotbarSize; i++) hotbar.Add(MakeSlot(i));
        return window;
    }

    private static GridPanel NewGrid() => new() { Columns = Inventory.Columns, CellSize = new Point(ItemSlot.SlotSize, ItemSlot.SlotSize), Spacing = 2 };

    private ItemSlot MakeSlot(int index)
    {
        var slot = new ItemSlot(() => _inventory[index], stack => _inventory[index] = stack);
        slot.Clicked = (_, click) =>
        {
            if (click.Button != MouseButton.Left) return;
            if (click.Shift) _inventory.QuickMove(index);
            else if (index < Inventory.HotbarSize) _inventory.SelectedSlot = index;   // clicking a hotbar slot picks it as the held item
        };
        return slot;
    }

    // ---- The creative palette ----

    private Widget BuildPalette()
    {
        var window = new StackPanel { Background = true, Padding = 8, Spacing = 5 };
        window.Add(new Label("Blocks") { Color = UiTheme.Accent });

        var grid = window.Add(NewGrid());
        for (int i = 0; i < PaletteSize; i++)
        {
            int cell = i;
            var slot = new ItemSlot(() => PaletteStack(cell), _ => { }) { Infinite = true };
            slot.Clicked = (_, click) =>
            {
                var stack = PaletteStack(cell);
                if (!stack.IsEmpty) _inventory.Add(stack.Block, click.Button == MouseButton.Left ? ItemStack.MaxCount : 1);
            };
            grid.Add(slot);
        }

        // Page controls, only shown if there are more blocks than fit on one page.
        int pages = (_palette.Length + PaletteSize - 1) / PaletteSize;
        if (pages > 1)
        {
            var controls = window.Add(new StackPanel { Vertical = false, Spacing = 6, CenterChildren = true });
            controls.Add(new Button("<", () => SetPage(_page - 1), 20, 14));
            _pageLabel = controls.Add(new Label());
            controls.Add(new Button(">", () => SetPage(_page + 1), 20, 14));
            SetPage(0);
        }
        return window;
    }

    private void SetPage(int page)
    {
        int pages = (_palette.Length + PaletteSize - 1) / PaletteSize;
        _page = (page + pages) % pages;
        if (_pageLabel != null) _pageLabel.Text = $"Page {_page + 1} / {pages}";
    }

    private ItemStack PaletteStack(int cell)
    {
        int index = _page * PaletteSize + cell;
        return index < _palette.Length ? new ItemStack(_palette[index], ItemStack.MaxCount) : ItemStack.Empty;
    }
}
