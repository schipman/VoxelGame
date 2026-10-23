using VoxelGame.Blocks;

namespace VoxelGame.Terrain;

/// <summary>Every biome. Where each one appears is decided by <see cref="BiomeSource"/>; how it looks by <see cref="BiomeRegistry"/>.</summary>
public enum Biome : byte
{
    Ocean,
    River,
    Lake,
    Beach,
    Plains,
    SnowyPlains,
    Forest,
    BirchForest,
    DarkForest,
    PaleGarden,
    Taiga,
    SnowyTaiga,
    Savanna,
    Desert,
    Mountains,
    StonyPeaks,
    SnowyPeaks,
}

/// <summary>What grows on the surface. The generator knows how to build each kind (see TreeBuilder).</summary>
public enum TreeKind { Oak, Birch, Spruce, DarkOak, PaleOak, Acacia, Cactus }

/// <summary>
/// Static description of a biome: its ground layers and what grows on it.
/// Layers from the top down: one Top block, FillerDepth blocks of Filler, DeepDepth blocks of Deep, then stone.
/// Special cases that depend on slope or height (bare rock on mountains, snow caps) are in WorldGenerator.
/// </summary>
public sealed record BiomeDef(
    string Name,
    BlockType Top,
    BlockType Filler,
    int FillerDepth,
    BlockType Deep = BlockType.Stone,
    int DeepDepth = 0,
    float TreeChance = 0f,                       // chance per 4x4 block cell that a tree/cactus starts in it
    (TreeKind Kind, int Weight)[] Trees = null);

public static class BiomeRegistry
{
    private static readonly BiomeDef[] Defs = new BiomeDef[Enum.GetValues<Biome>().Length];

    static BiomeRegistry()
    {
        Register(Biome.Ocean,       new("Ocean",        BlockType.Sand,       BlockType.Sand, 3));
        Register(Biome.River,       new("River",        BlockType.Sand,       BlockType.Sand, 3));
        Register(Biome.Lake,        new("Lake",         BlockType.Sand,       BlockType.Sand, 3));
        Register(Biome.Beach,       new("Beach",        BlockType.Sand,       BlockType.Sand, 3));
        Register(Biome.Plains,      new("Plains",       BlockType.Grass,      BlockType.Dirt, 3,
                                        TreeChance: 0.02f, Trees: new[] { (TreeKind.Oak, 1) }));
        Register(Biome.SnowyPlains, new("Snowy Plains", BlockType.SnowyGrass, BlockType.Dirt, 3,
                                        TreeChance: 0.012f, Trees: new[] { (TreeKind.Spruce, 1) }));
        Register(Biome.Forest,      new("Forest",       BlockType.Grass,      BlockType.Dirt, 3,
                                        TreeChance: 0.5f, Trees: new[] { (TreeKind.Oak, 5), (TreeKind.Birch, 1) }));
        Register(Biome.BirchForest, new("Birch Forest", BlockType.Grass,      BlockType.Dirt, 3,
                                        TreeChance: 0.45f, Trees: new[] { (TreeKind.Birch, 1) }));
        Register(Biome.DarkForest,  new("Dark Forest",  BlockType.Grass,      BlockType.Dirt, 3,
                                        TreeChance: 0.5f, Trees: new[] { (TreeKind.DarkOak, 1) }));
        Register(Biome.PaleGarden,  new("Pale Garden",  BlockType.PaleMoss,   BlockType.Dirt, 3,
                                        TreeChance: 0.28f, Trees: new[] { (TreeKind.PaleOak, 1) }));
        Register(Biome.Taiga,       new("Taiga",        BlockType.Grass,      BlockType.Dirt, 3,
                                        TreeChance: 0.35f, Trees: new[] { (TreeKind.Spruce, 1) }));
        Register(Biome.SnowyTaiga,  new("Snowy Taiga",  BlockType.SnowyGrass, BlockType.Dirt, 3,
                                        TreeChance: 0.3f, Trees: new[] { (TreeKind.Spruce, 1) }));
        Register(Biome.Savanna,     new("Savanna",      BlockType.DryGrass,   BlockType.Dirt, 3,
                                        TreeChance: 0.045f, Trees: new[] { (TreeKind.Acacia, 1) }));
        Register(Biome.Desert,      new("Desert",       BlockType.Sand,       BlockType.Sand, 3, BlockType.Sandstone, 5,
                                        TreeChance: 0.035f, Trees: new[] { (TreeKind.Cactus, 1) }));
        Register(Biome.Mountains,   new("Mountains",    BlockType.Grass,      BlockType.Dirt, 3,
                                        TreeChance: 0.06f, Trees: new[] { (TreeKind.Spruce, 3), (TreeKind.Oak, 1) }));
        Register(Biome.StonyPeaks,  new("Stony Peaks",  BlockType.Stone,      BlockType.Stone, 3));
        Register(Biome.SnowyPeaks,  new("Snowy Peaks",  BlockType.Snow,       BlockType.Snow, 2));
    }

    private static void Register(Biome biome, BiomeDef def) => Defs[(int)biome] = def;

    public static BiomeDef Get(Biome biome) => Defs[(int)biome];
}
