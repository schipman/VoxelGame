namespace VoxelGame.Persistence;

/// <summary>Everything about a world that isn't block data. Saved as level.json.</summary>
public sealed class LevelData
{
    public int Version { get; set; } = 1;
    public int Seed { get; set; }

    /// <summary>Which version of the terrain generator made this world (see WorldGenerator.Version).</summary>
    public int GeneratorVersion { get; set; }
    public float TimeOfDay { get; set; }

    public float PlayerX { get; set; }
    public float PlayerY { get; set; }
    public float PlayerZ { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public bool Flying { get; set; }
}
