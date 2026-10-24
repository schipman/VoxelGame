namespace VoxelGame.Persistence;

/// <summary>One player's state in a world: where they stood and what they carried. Saved as players/{name}.json.</summary>
public sealed class PlayerSave
{
    public string Name { get; set; } = "Player";

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public bool Flying { get; set; }

    /// <summary>The non-empty inventory slots, or null if the player hasn't got one saved yet (they get the starting items).</summary>
    public List<InventorySlotData> Inventory { get; set; }
    public int SelectedSlot { get; set; }

    /// <summary>Builds a save from a world saved before multiplayer, which kept a single player's state in level.json. Null if there isn't one.</summary>
    public static PlayerSave FromLegacy(LevelData level, string name)
    {
        if (level == null || level.PlayerY <= 0) return null;
        return new PlayerSave
        {
            Name = name,
            X = level.PlayerX, Y = level.PlayerY, Z = level.PlayerZ,
            Yaw = level.Yaw, Pitch = level.Pitch, Flying = level.Flying,
            Inventory = level.Inventory,
            SelectedSlot = level.SelectedSlot,
        };
    }
}
