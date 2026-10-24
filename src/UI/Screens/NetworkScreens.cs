using Microsoft.Xna.Framework;

namespace VoxelGame.UI.Screens;

/// <summary>What the title screen can ask the game to do.</summary>
public sealed class TitleActions
{
    public Action NewWorld;
    public Action LoadWorld;
    public Action JoinGame;
    public Action Settings;
    public Action Quit;
}

/// <summary>The first thing you see: start a world, load one, join a friend, or quit.</summary>
public sealed class TitleScreen : UiScreen
{
    public override bool ClosesOnEscape => false;

    public TitleScreen(TitleActions actions, string message = null)
    {
        var menu = Root.Add(new StackPanel { Background = true, Padding = 12, Spacing = 5, CenterChildren = true, Anchor = Anchor.Center });
        menu.Add(new Label("VoxelGame") { Scale = 4, Color = UiTheme.Accent });
        menu.Add(new Label("") { Size = new Point(1, 6) });
        if (!string.IsNullOrEmpty(message))
        {
            menu.Add(new Label(message) { Color = new Color(255, 120, 110) });
            menu.Add(new Label("") { Size = new Point(1, 4) });
        }
        menu.Add(new Button("Singleplayer: new world", actions.NewWorld, 180));
        menu.Add(new Button("Load world", actions.LoadWorld, 180));
        menu.Add(new Button("Join game", actions.JoinGame, 180));
        menu.Add(new Button("Settings", actions.Settings, 180));
        menu.Add(new Button("Quit", actions.Quit, 180));
    }
}

/// <summary>A list of saved worlds to pick from.</summary>
public sealed class WorldListScreen : UiScreen
{
    public WorldListScreen(IReadOnlyList<(string Label, int Seed)> worlds, Action<int> chosen)
    {
        var menu = Root.Add(new StackPanel { Background = true, Padding = 10, Spacing = 4, CenterChildren = true, Anchor = Anchor.Center });
        menu.Add(new Label("Load world") { Scale = 2, Color = UiTheme.Accent });
        menu.Add(new Label("") { Size = new Point(1, 4) });

        if (worlds.Count == 0) menu.Add(new Label("No saved worlds yet.") { Color = UiTheme.TextDim });
        foreach (var (label, seed) in worlds.Take(8))
            menu.Add(new Button(label, () => chosen(seed), 220));

        menu.Add(new Label("") { Size = new Point(1, 2) });
        menu.Add(new Button("Back", () => Ui.CloseTop(), 220));
    }
}

/// <summary>Type an address and a name, and join someone's game.</summary>
public sealed class JoinScreen : UiScreen
{
    private readonly TextBox _address;
    private readonly TextBox _name;
    private readonly Label _error;

    public JoinScreen(string lastAddress, string playerName, Action<string, string> join)
    {
        var menu = Root.Add(new StackPanel { Background = true, Padding = 10, Spacing = 4, CenterChildren = true, Anchor = Anchor.Center });
        menu.Add(new Label("Join game") { Scale = 2, Color = UiTheme.Accent });
        menu.Add(new Label("") { Size = new Point(1, 4) });

        menu.Add(new Label("Server address (IP or name, optional :port)") { Color = UiTheme.TextDim });
        _address = menu.Add(new TextBox(220) { Text = lastAddress ?? "", Placeholder = "192.168.1.5", MaxLength = 64 });
        menu.Add(new Label("Your name") { Color = UiTheme.TextDim });
        _name = menu.Add(new TextBox(220) { Text = playerName ?? "", MaxLength = 16, AllowedChar = c => char.IsLetterOrDigit(c) || c == '_' || c == ' ' });
        _error = menu.Add(new Label("") { Color = new Color(255, 120, 110) });

        void Go()
        {
            if (_address.Text.Trim().Length == 0) { _error.Text = "Enter the server's address."; _address.Focus(); return; }
            if (_name.Text.Trim().Length == 0) { _error.Text = "Enter a name."; _name.Focus(); return; }
            join(_address.Text.Trim(), _name.Text.Trim());
        }
        _address.Submitted = Go;
        _name.Submitted = Go;

        menu.Add(new Button("Join", Go, 220));
        menu.Add(new Button("Back", () => Ui.CloseTop(), 220));
    }

    public override void OnOpened() => _address.Focus();
    public override void OnClosed() => TextBox.ReleaseFocus();

    public void ShowError(string text) => _error.Text = text;
}

/// <summary>"Connecting..." / "Loading terrain...": shown until the game can start, with a way out.</summary>
public sealed class StatusScreen : UiScreen
{
    private readonly Label _text;

    public override bool ClosesOnEscape => false;
    public override bool DimBackground => false;

    public StatusScreen(string title, Action cancel)
    {
        var menu = Root.Add(new StackPanel { Background = true, Padding = 12, Spacing = 6, CenterChildren = true, Anchor = Anchor.Center });
        menu.Add(new Label(title) { Scale = 2, Color = UiTheme.Accent });
        _text = menu.Add(new Label("...") { Color = UiTheme.Text });
        menu.Add(new Label("") { Size = new Point(1, 2) });
        menu.Add(new Button("Cancel", cancel, 120));
    }

    public string Text { get => _text.Text; set { if (_text.Text != value) _text.Text = value; } }
}
