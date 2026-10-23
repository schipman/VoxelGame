using Microsoft.Xna.Framework;
using VoxelGame.Items;

namespace VoxelGame.UI;

/// <summary>
/// The always-visible layer: crosshair, hotbar, the name of the item you just picked up, debug text and short
/// messages. It is just a widget tree like any screen, except it never takes mouse input.
/// </summary>
public sealed class Hud
{
    private const float ToastSeconds = 1.6f;

    private readonly Inventory _inventory;
    private readonly Crosshair _crosshair;
    private readonly Label _debug;
    private readonly Label _itemName;
    private readonly Label _message;
    private readonly ItemSlot[] _hotbar = new ItemSlot[Inventory.HotbarSize];

    private float _itemNameTimer;
    private float _messageTimer;

    public Widget Root { get; } = new ScreenRoot();

    /// <summary>Hide the crosshair while a menu is open (the UiManager keeps this up to date).</summary>
    public bool ScreenOpen { get; set; }

    public bool DebugVisible { get => _debug.Visible; set => _debug.Visible = value; }
    public string DebugText { get => _debug.Text; set => _debug.Text = value; }

    public Hud(Inventory inventory)
    {
        _inventory = inventory;

        _crosshair = Root.Add(new Crosshair { Anchor = Anchor.Center });

        _debug = Root.Add(new Label { Anchor = Anchor.TopLeft, Offset = new Point(4, 4), Color = UiTheme.Text });

        var bar = Root.Add(new StackPanel { Vertical = false, Spacing = 2, Padding = 3, Background = true, Anchor = Anchor.Bottom, Offset = new Point(0, -3) });
        for (int i = 0; i < _hotbar.Length; i++)
        {
            int slot = i;
            _hotbar[i] = bar.Add(new ItemSlot(() => _inventory[slot], stack => _inventory[slot] = stack) { Interactive = false });
        }

        _itemName = Root.Add(new Label { Anchor = Anchor.Bottom, Offset = new Point(0, -34), Color = UiTheme.Text });
        _message = Root.Add(new Label { Anchor = Anchor.Top, Offset = new Point(0, 12), Color = UiTheme.Accent });
    }

    /// <summary>Shows an item's name above the hotbar for a moment (call when the selection changes).</summary>
    public void ShowItemName(string name)
    {
        _itemName.Text = name;
        _itemNameTimer = string.IsNullOrEmpty(name) ? 0f : ToastSeconds;
    }

    /// <summary>Shows a message at the top of the screen for a few seconds.</summary>
    public void ShowMessage(string text, float seconds)
    {
        _message.Text = text;
        _messageTimer = seconds;
    }

    public void Update(float dt)
    {
        _crosshair.Visible = !ScreenOpen;

        for (int i = 0; i < _hotbar.Length; i++)
            _hotbar[i].Highlighted = i == _inventory.SelectedSlot;

        _itemNameTimer = Math.Max(0, _itemNameTimer - dt);
        _itemName.Opacity = Math.Clamp(_itemNameTimer / 0.5f, 0f, 1f);

        _messageTimer = Math.Max(0, _messageTimer - dt);
        _message.Opacity = Math.Clamp(_messageTimer / 0.5f, 0f, 1f);
    }

    /// <summary>A small plus in the middle of the screen.</summary>
    private sealed class Crosshair : Widget
    {
        public Crosshair() => Size = new Point(9, 9);
        public override bool HitTestVisible => false;

        protected override void DrawSelf(UiRenderer r)
        {
            var p = AbsolutePosition;
            var line = UiTheme.Alpha(255, 255, 255, 215);
            // A dark rim first so it shows against bright sky and snow, then the white plus.
            r.Fill(new Rectangle(p.X - 1, p.Y + 3, 11, 3), UiTheme.Alpha(0, 0, 0, 90));
            r.Fill(new Rectangle(p.X + 3, p.Y - 1, 3, 11), UiTheme.Alpha(0, 0, 0, 90));
            r.Fill(new Rectangle(p.X, p.Y + 4, 9, 1), line);
            r.Fill(new Rectangle(p.X + 4, p.Y, 1, 9), line);
        }
    }
}
