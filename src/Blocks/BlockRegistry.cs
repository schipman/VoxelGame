namespace VoxelGame.Blocks;

/// <summary>
/// A tile slot in Assets/Textures/blocks.png. The number is the slot: column = number % 16, row = number / 16.
/// To add a texture, draw it in the next free slot of the PNG and add an entry here with that number.
/// </summary>
public enum Tile
{
    GrassTop  = 0,   // column 0, row 0
    GrassSide = 1,
    Dirt      = 2,
    Stone     = 3,
    Sand      = 4,
    Bedrock   = 5,
    Lamp      = 6,
    DimLamp   = 7,

    Gravel          = 8,
    Snow            = 9,
    SnowyGrassSide  = 10,
    PodzolTop       = 11,
    PodzolSide      = 12,
    DryGrassTop     = 13,
    DryGrassSide    = 14,
    PaleMossTop     = 15,
    PaleMossSide    = 16,
    SandstoneTop    = 17,
    SandstoneSide   = 18,
    CactusTop       = 19,
    CactusSide      = 20,

    OakLogEnd       = 21,
    OakLogSide      = 22,
    BirchLogEnd     = 23,
    BirchLogSide    = 24,
    SpruceLogEnd    = 25,
    SpruceLogSide   = 26,
    DarkOakLogEnd   = 27,
    DarkOakLogSide  = 28,
    PaleOakLogEnd   = 29,
    PaleOakLogSide  = 30,
    AcaciaLogEnd    = 31,
    AcaciaLogSide   = 32,

    OakLeaves       = 33,
    BirchLeaves     = 34,
    SpruceLeaves    = 35,
    DarkOakLeaves   = 36,
    PaleOakLeaves   = 37,
    AcaciaLeaves    = 38,
    // Next free slot: 39. The atlas has 256 slots (16 columns x 16 rows).
}

/// <summary>Static description of a block type: how it looks and how it behaves.</summary>
public sealed record BlockDef(
    string Name,
    bool IsSolid,    // blocks player movement
    bool IsOpaque,   // hides the faces of neighbouring blocks and fully blocks light
    Tile Top,
    Tile Side,
    Tile Bottom,
    int LightEmission = 0,             // 0-15, the light level the block gives off
    bool CullsSameNeighbor = false,    // skip the face between two of these (leaves: hides the hollow inside a canopy)
    bool DimsSkylight = false,         // sunlight passing straight down loses a level (leaves, water)
    int LightAbsorb = 0,               // extra light lost passing through, on top of the usual 1 per block (water)
    bool IsLiquid = false);            // drawn by the water pass, flows, and can be swum in

public static class BlockRegistry
{
    private static readonly BlockDef[] Defs = new BlockDef[256];
    private static readonly bool[] Opaque = new bool[256];
    private static readonly bool[] CullsSame = new bool[256];
    private static readonly bool[] DimsSky = new bool[256];
    private static readonly int[] Absorb = new int[256];
    private static readonly bool[] Liquid = new bool[256];

    static BlockRegistry()
    {
        Register(BlockType.Air,     new BlockDef("Air",      false, false, Tile.Stone,    Tile.Stone,     Tile.Stone));
        Register(BlockType.Grass,   new BlockDef("Grass",    true,  true,  Tile.GrassTop, Tile.GrassSide, Tile.Dirt));
        Register(BlockType.Dirt,    Cube("Dirt", Tile.Dirt));
        Register(BlockType.Stone,   Cube("Stone", Tile.Stone));
        Register(BlockType.Sand,    Cube("Sand", Tile.Sand));
        Register(BlockType.Bedrock, Cube("Bedrock", Tile.Bedrock));
        Register(BlockType.Lamp,    new BlockDef("Lamp",     true,  true,  Tile.Lamp,     Tile.Lamp,      Tile.Lamp,    LightEmission: 15));
        Register(BlockType.DimLamp, new BlockDef("Dim Lamp", true,  true,  Tile.DimLamp,  Tile.DimLamp,   Tile.DimLamp, LightEmission: 9));

        Register(BlockType.Gravel,     Cube("Gravel", Tile.Gravel));
        Register(BlockType.Snow,       Cube("Snow", Tile.Snow));
        Register(BlockType.SnowyGrass, new BlockDef("Snowy Grass", true, true, Tile.Snow,        Tile.SnowyGrassSide, Tile.Dirt));
        Register(BlockType.Podzol,     new BlockDef("Podzol",      true, true, Tile.PodzolTop,   Tile.PodzolSide,     Tile.Dirt));
        Register(BlockType.DryGrass,   new BlockDef("Dry Grass",   true, true, Tile.DryGrassTop, Tile.DryGrassSide,   Tile.Dirt));
        Register(BlockType.PaleMoss,   new BlockDef("Pale Moss",   true, true, Tile.PaleMossTop, Tile.PaleMossSide,   Tile.Dirt));
        Register(BlockType.Sandstone,  new BlockDef("Sandstone",   true, true, Tile.SandstoneTop, Tile.SandstoneSide, Tile.SandstoneTop));
        Register(BlockType.Cactus,     new BlockDef("Cactus",      true, true, Tile.CactusTop,   Tile.CactusSide,     Tile.CactusTop));

        Register(BlockType.OakLog,     Log("Oak Log",      Tile.OakLogEnd,     Tile.OakLogSide));
        Register(BlockType.BirchLog,   Log("Birch Log",    Tile.BirchLogEnd,   Tile.BirchLogSide));
        Register(BlockType.SpruceLog,  Log("Spruce Log",   Tile.SpruceLogEnd,  Tile.SpruceLogSide));
        Register(BlockType.DarkOakLog, Log("Dark Oak Log", Tile.DarkOakLogEnd, Tile.DarkOakLogSide));
        Register(BlockType.PaleOakLog, Log("Pale Oak Log", Tile.PaleOakLogEnd, Tile.PaleOakLogSide));
        Register(BlockType.AcaciaLog,  Log("Acacia Log",   Tile.AcaciaLogEnd,  Tile.AcaciaLogSide));

        Register(BlockType.OakLeaves,     Leaves("Oak Leaves",      Tile.OakLeaves));
        Register(BlockType.BirchLeaves,   Leaves("Birch Leaves",    Tile.BirchLeaves));
        Register(BlockType.SpruceLeaves,  Leaves("Spruce Leaves",   Tile.SpruceLeaves));
        Register(BlockType.DarkOakLeaves, Leaves("Dark Oak Leaves", Tile.DarkOakLeaves));
        Register(BlockType.PaleOakLeaves, Leaves("Pale Oak Leaves", Tile.PaleOakLeaves));
        Register(BlockType.AcaciaLeaves,  Leaves("Acacia Leaves",   Tile.AcaciaLeaves));

        // Water has no texture of its own: the water shader draws it. It absorbs light, so the sea floor fades to dark.
        Register(BlockType.Water, new BlockDef("Water", false, false, Tile.Stone, Tile.Stone, Tile.Stone, DimsSkylight: true, LightAbsorb: 1, IsLiquid: true));
    }

    // Shorthands for the common block shapes.
    private static BlockDef Cube(string name, Tile tile) => new(name, true, true, tile, tile, tile);
    private static BlockDef Log(string name, Tile end, Tile side) => new(name, true, true, end, side, end);

    /// <summary>Leaves let light through (dimming it) and hide the faces between neighbouring leaf blocks.</summary>
    private static BlockDef Leaves(string name, Tile tile) =>
        new(name, true, false, tile, tile, tile, CullsSameNeighbor: true, DimsSkylight: true);

    private static void Register(BlockType type, BlockDef def)
    {
        Defs[(int)type] = def;
        Opaque[(int)type] = def.IsOpaque;
        CullsSame[(int)type] = def.CullsSameNeighbor;
        DimsSky[(int)type] = def.DimsSkylight || def.LightAbsorb > 0;
        Absorb[(int)type] = def.LightAbsorb;
        Liquid[(int)type] = def.IsLiquid;
    }

    public static BlockDef Get(BlockType type) => Defs[(int)type];

    // Array lookups for hot loops (lighting and meshing).
    public static bool IsOpaque(BlockType type) => Opaque[(int)type];
    public static bool CullsSameNeighbor(BlockType type) => CullsSame[(int)type];
    public static bool DimsSkylight(BlockType type) => DimsSky[(int)type];
    public static int LightAbsorb(BlockType type) => Absorb[(int)type];
    public static bool IsLiquid(BlockType type) => Liquid[(int)type];
}
