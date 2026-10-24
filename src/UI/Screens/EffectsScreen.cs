using VoxelGame.Settings;

namespace VoxelGame.UI.Screens;

/// <summary>The post-processing options (bloom, tone mapping, saturation, vignette). Changes apply at once; the settings screen saves them.</summary>
public sealed class EffectsScreen : UiScreen
{
    private static readonly string[] TonemapNames = { "Off", "AgX", "AgX punchy" };

    private readonly GameSettings _settings;

    public override bool PausesGame => true;

    public EffectsScreen(GameSettings settings)
    {
        _settings = settings;

        var menu = Root.Add(new StackPanel { Background = true, Padding = 10, Spacing = 4, CenterChildren = true, Anchor = Anchor.Center });
        menu.Add(new Label("Visual effects") { Scale = 2, Color = UiTheme.Accent });
        menu.Add(new Label("") { Size = new Microsoft.Xna.Framework.Point(1, 4) });

        menu.Add(new Slider("Bloom", 0, 100, settings.Bloom, v => settings.Bloom = v, v => v == 0 ? "Off" : $"{v}%"));
        menu.Add(new Slider("God rays", 0, 100, settings.GodRays, v => settings.GodRays = v, v => v == 0 ? "Off" : $"{v}%"));
        menu.Add(new CycleButton("Tone mapping", TonemapNames, settings.Tonemap, i => settings.Tonemap = i));
        menu.Add(new Slider("Exposure", 25, 400, settings.TonemapExposure, v => settings.TonemapExposure = v, v => $"{v}%"));
        menu.Add(new Slider("Saturation", 0, 200, settings.Saturation, v => settings.Saturation = v, v => $"{v}%"));
        menu.Add(new Slider("Vignette", 0, 100, settings.Vignette, v => settings.Vignette = v, v => v == 0 ? "Off" : $"{v}%"));
        menu.Add(new Label("") { Size = new Microsoft.Xna.Framework.Point(1, 2) });
        menu.Add(new Button("Done", () => Ui.CloseTop()));
    }

    public override void OnClosed()
    {
        _settings.Clamp();
        _settings.Save(GameSettings.DefaultPath);
    }
}
