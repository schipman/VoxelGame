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

    /// <summary>The non-empty inventory slots. Null in worlds saved before the inventory existed.</summary>
    public List<InventorySlotData> Inventory { get; set; }
    public int SelectedSlot { get; set; }
}

/// <summary>One inventory slot in the level file. The block is stored by name so saves survive new blocks being added.</summary>
public sealed class InventorySlotData
{
    public int Slot { get; set; }
    public string Block { get; set; }
    public int Count { get; set; }
}
