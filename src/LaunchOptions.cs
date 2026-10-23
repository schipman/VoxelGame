namespace VoxelGame;

/// <summary>
/// Optional command-line switches, mainly for testing: <c>dotnet run -- --time=0.5 --yaw=90 --pitch=-20 --lamp</c>
///   --time=T      start time of day (0..1; 0.5 = noon, 0 = midnight)
///   --yaw=DEG     initial camera yaw in degrees
///   --pitch=DEG   initial camera pitch in degrees
///   --seed=N      world seed (default: a new random seed every launch). Reuse a seed to reload that world's saved edits.
///   --find=BIOME  start in the nearest biome of that name, e.g. --find=PaleGarden (new worlds only)
///   --tp=X,Z      start at those world coordinates
///   --lamp        place a lamp on the ground in front of the spawn point
/// </summary>
public sealed class LaunchOptions
{
    public float? Time { get; private set; }
    public float? Yaw { get; private set; }
    public float? Pitch { get; private set; }
    public int? Seed { get; private set; }
    public Terrain.Biome? FindBiome { get; private set; }
    public (int X, int Z)? Teleport { get; private set; }
    public bool Lamp { get; private set; }

    public static LaunchOptions Parse(string[] args)
    {
        var options = new LaunchOptions();
        foreach (var arg in args)
        {
            var parts = arg.TrimStart('-').Split('=', 2);
            float.TryParse(parts.Length > 1 ? parts[1] : "", System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value);
            bool hasValue = parts.Length > 1;

            switch (parts[0])
            {
                case "time" when hasValue: options.Time = value; break;
                case "yaw" when hasValue: options.Yaw = value; break;
                case "pitch" when hasValue: options.Pitch = value; break;
                case "seed" when hasValue && int.TryParse(parts[1], out int seed): options.Seed = seed; break;
                case "find" when hasValue:
                    if (Enum.TryParse<Terrain.Biome>(parts[1].Replace(" ", ""), ignoreCase: true, out var biome)) options.FindBiome = biome;
                    else Console.WriteLine($"Unknown biome '{parts[1]}'. Options: {string.Join(", ", Enum.GetNames<Terrain.Biome>())}");
                    break;
                case "tp" when hasValue:
                    var xz = parts[1].Split(',');
                    if (xz.Length == 2 && int.TryParse(xz[0], out int tx) && int.TryParse(xz[1], out int tz)) options.Teleport = (tx, tz);
                    break;
                case "lamp": options.Lamp = true; break;
            }
        }
        return options;
    }
}
