using VoxelGame.Settings;

namespace VoxelGame.UI.Screens;

/// <summary>
/// Sliders and toggles for GameSettings. Changes apply immediately (the game reads the settings every frame) and are
/// written to disk when the screen closes. To add an option: add a property to GameSettings, then a control here.
/// </summary>
public sealed class SettingsScreen : UiScreen
{
    private static readonly string[] UiScaleNames = { "Auto", "1", "2", "3", "4", "5", "6" };
    private static readonly string[] OnOff = { "Off", "On" };

    private readonly GameSettings _settings;

    public override bool PausesGame => true;

    public SettingsScreen(GameSettings settings)
    {
        _settings = settings;

        var menu = Root.Add(new StackPanel { Background = true, Padding = 10, Spacing = 4, CenterChildren = true, Anchor = Anchor.Center });
        menu.Add(new Label("Settings") { Scale = 2, Color = UiTheme.Accent });
        menu.Add(new Label("") { Size = new Microsoft.Xna.Framework.Point(1, 4) });

        menu.Add(new Slider("Render distance", 3, 20, settings.RenderDistance, v => settings.RenderDistance = v, v => $"{v} chunks"));
        menu.Add(new Slider("Field of view", 40, 120, settings.FieldOfView, v => settings.FieldOfView = v, v => $"{v}"));
        menu.Add(new Slider("Mouse sensitivity", 10, 300, settings.MouseSensitivity, v => settings.MouseSensitivity = v, v => $"{v}%"));
        menu.Add(new CycleButton("UI scale", UiScaleNames, settings.UiScale, i => settings.UiScale = i));
        menu.Add(new CycleButton("Fullscreen", OnOff, settings.Fullscreen ? 1 : 0, i => settings.Fullscreen = i == 1));
        menu.Add(new CycleButton("Shadows", OnOff, settings.Shadows ? 1 : 0, i => settings.Shadows = i == 1));
        menu.Add(new CycleButton("Debug text", OnOff, settings.ShowDebugText ? 1 : 0, i => settings.ShowDebugText = i == 1));
        menu.Add(new CycleButton("Game mode", new[] { "Creative", "Survival" }, (int)settings.GameMode, i => settings.GameMode = (GameMode)i));
        menu.Add(new Label("") { Size = new Microsoft.Xna.Framework.Point(1, 2) });
        menu.Add(new Button("Done", () => Ui.CloseTop()));
    }

    public override void OnClosed()
    {
        _settings.Clamp();
        _settings.Save(GameSettings.DefaultPath);
    }
}
