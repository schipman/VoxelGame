using VoxelGame.Settings;

namespace VoxelGame.UI.Screens;

/// <summary>What the pause menu can ask the game to do.</summary>
public sealed class PauseActions
{
    public Action SaveGame;
    public Action SaveAndQuit;
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
        menu.Add(new Label("") { Size = new Microsoft.Xna.Framework.Point(1, 4) });   // a little gap under the title
        menu.Add(new Button("Resume", () => Ui.CloseTop()));
        menu.Add(new Button("Settings", () => Ui.Open(new SettingsScreen(_settings))));
        menu.Add(new Button("Save Game", () => _actions.SaveGame?.Invoke()));
        menu.Add(new Button("Save and Quit", () => _actions.SaveAndQuit?.Invoke()));
    }
}
