using System.Text.Json;

namespace VoxelGame.Settings;

public enum GameMode { Creative, Survival }

/// <summary>
/// Options the player can change from the settings menu. Saved to %AppData%\VoxelGame\settings.json and shared by all
/// worlds. To add one: add a property here, read it where it matters, and add a control in SettingsScreen.
/// </summary>
public sealed class GameSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoxelGame", "settings.json");

    public int RenderDistance { get; set; } = 8;            // chunks
    public int FieldOfView { get; set; } = 70;              // degrees
    public int MouseSensitivity { get; set; } = 100;        // percent
    public int UiScale { get; set; } = 0;                   // 0 = automatic from the window size
    public bool Fullscreen { get; set; } = false;
    public bool Shadows { get; set; } = true;
    public bool ShowDebugText { get; set; } = true;
    public GameMode GameMode { get; set; } = GameMode.Creative;

    /// <summary>The name other players see. Letters, digits, spaces and underscores, up to 16.</summary>
    public string PlayerName { get; set; } = DefaultName();
    public string LastServer { get; set; } = "";

    private static string DefaultName()
    {
        var name = new string(Environment.UserName.Where(c => char.IsLetterOrDigit(c) || c == '_').Take(16).ToArray());
        return name.Length > 0 ? name : "Player";
    }

    public static GameSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path)) ?? new GameSettings();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Console.WriteLine($"Could not read settings ({e.Message}); using defaults.");
        }
        return new GameSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException e)
        {
            Console.WriteLine($"Could not save settings: {e.Message}");
        }
    }

    /// <summary>Brings values into a sensible range, in case the file was edited by hand.</summary>
    public void Clamp()
    {
        RenderDistance = Math.Clamp(RenderDistance, 3, 20);
        PlayerName = Server.GameServer.CleanName(PlayerName) ?? DefaultName();
        LastServer ??= "";
        FieldOfView = Math.Clamp(FieldOfView, 40, 120);
        MouseSensitivity = Math.Clamp(MouseSensitivity, 10, 300);
        UiScale = Math.Clamp(UiScale, 0, 6);
    }
}
