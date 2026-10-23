namespace VoxelGame.UI;

/// <summary>
/// A full-window menu: the pause menu, the inventory and so on. Build it by adding widgets to <see cref="Root"/>
/// (usually one anchored panel in the middle) and open it with <c>UiManager.Open</c>. Screens stack, so a settings
/// screen opened from the pause menu sits on top of it and closing it returns to the pause menu.
/// </summary>
public abstract class UiScreen
{
    /// <summary>The widgets of this screen. Its size is kept equal to the window (in UI pixels).</summary>
    public Widget Root { get; } = new ScreenRoot();

    /// <summary>The game stands still while this screen is open.</summary>
    public virtual bool PausesGame => false;

    /// <summary>Darken the world behind it.</summary>
    public virtual bool DimBackground => true;

    /// <summary>Pressing Escape closes it.</summary>
    public virtual bool ClosesOnEscape => true;

    protected UiManager Ui { get; private set; }

    internal void Attach(UiManager ui) => Ui = ui;

    /// <summary>Called when the screen appears; rebuild anything that depends on current state here.</summary>
    public virtual void OnOpened() { }

    public virtual void OnClosed() { }
}
