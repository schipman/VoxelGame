namespace VoxelGame.Blocks;

/// <summary>
/// Every kind of block. Stored as one byte per voxel, so keep it under 256 entries.
/// Saves store block names, so entries can be added or reordered safely (but renaming one orphans old saves).
/// </summary>
public enum BlockType : byte
{
    Air,
    Grass,
    Dirt,
    Stone,
    Sand,
    Bedrock,
    Lamp,
    DimLamp,

    // Terrain surfaces
    Gravel,
    Snow,
    SnowyGrass,
    Podzol,
    DryGrass,
    PaleMoss,
    Sandstone,
    Cactus,

    // Wood
    OakLog,
    BirchLog,
    SpruceLog,
    DarkOakLog,
    PaleOakLog,
    AcaciaLog,

    // Leaves
    OakLeaves,
    BirchLeaves,
    SpruceLeaves,
    DarkOakLeaves,
    PaleOakLeaves,
    AcaciaLeaves,

    // Liquids
    Water,
}
