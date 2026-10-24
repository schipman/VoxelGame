using VoxelGame.Settings;

namespace VoxelGame.UI.Screens;

/// <summary>What the pause menu can ask the game to do.</summary>
public sealed class PauseActions
{
    public Action SaveGame;
    public Action SaveAndQuit;       // back to the title screen

    /// <summary>Null when the game can't be opened to others (already open, or joined to someone else's).</summary>
    public Action OpenToLan;

    /// <summary>Shown under the title when others can join, e.g. the addresses to give them.</summary>
    public string HostInfo;
}

/// <summary>The menu Escape opens: resume, settings, save, quit. The game stands still behind it.</summary>
public sealed class PauseScreen : UiScreen
{
    private readonly GameSettings _settings;
    private readonly PauseActions _actions;

    public override bool PausesGame => true;

    public PauseScreen(GameSettings settings, PauseActions actions)
    {
        _settings = settings;
        _actions = actions;

        var menu = Root.Add(new StackPanel { Background = true, Padding = 10, Spacing = 5, CenterChildren = true, Anchor = Anchor.Center });
        menu.Add(new Label("Game Paused") { Scale = 2, Color = UiTheme.Accent });
        if (!string.IsNullOrEmpty(_actions.HostInfo)) menu.Add(new Label(_actions.HostInfo) { Color = UiTheme.TextDim });
        menu.Add(new Label("") { Size = new Microsoft.Xna.Framework.Point(1, 4) });   // a little gap under the title
        menu.Add(new Button("Resume", () => Ui.CloseTop()));
        menu.Add(new Button("Settings", () => Ui.Open(new SettingsScreen(_settings))));
        if (_actions.SaveGame != null) menu.Add(new Button("Save Game", () => _actions.SaveGame()));
        if (_actions.OpenToLan != null) menu.Add(new Button("Open to LAN", () => _actions.OpenToLan()));
        menu.Add(new Button(_actions.SaveGame != null ? "Save and Quit to Title" : "Leave Game", () => _actions.SaveAndQuit?.Invoke(), 150));
    }
}
