namespace VoxelGame.Terrain;

/// <summary>
/// Chooses a biome from the climate, following the shape of Minecraft's system:
///   1. low continentalness is ocean, then beach;
///   2. inland, rugged high ground (low erosion, high peaks-and-valleys) becomes mountains and peaks;
///   3. everything else is picked from a temperature x humidity table.
/// </summary>
public static class BiomeSource
{
    // Continentalness edges.
    private const float OceanBelow = -0.32f;
    private const float BeachBelow = -0.2f;

    // [temperature level 0..4 (cold..hot)][humidity level 0..4 (dry..wet)]
    private static readonly Biome[][] Middle =
    {
        new[] { Biome.SnowyPlains, Biome.SnowyPlains, Biome.SnowyPlains, Biome.SnowyTaiga, Biome.SnowyTaiga },
        new[] { Biome.Plains,      Biome.Plains,      Biome.Forest,      Biome.Taiga,      Biome.Taiga      },
        new[] { Biome.Plains,      Biome.Plains,      Biome.Forest,      Biome.BirchForest, Biome.DarkForest },
        new[] { Biome.Savanna,     Biome.Savanna,     Biome.Forest,      Biome.Forest,     Biome.Forest     },
        new[] { Biome.Desert,      Biome.Desert,      Biome.Desert,      Biome.Savanna,    Biome.Savanna    },
    };

    public static Biome Select(Climate c)
    {
        if (c.Continentalness < OceanBelow) return Biome.Ocean;
        if (c.Continentalness < BeachBelow) return Biome.Beach;

        float pv = c.PeaksAndValleys;
        int temp = c.TemperatureLevel;

        // Rugged high ground inland.
        if (c.Continentalness > 0.2f && c.Erosion < -0.2f && pv > 0.45f)
            return temp <= 1 ? Biome.SnowyPeaks : Biome.StonyPeaks;
        if (c.Continentalness > 0.05f && c.Erosion < -0.1f && pv > 0.15f)
            return Biome.Mountains;

        var biome = Middle[temp][c.HumidityLevel];

        // Rare variant of the wettest temperate forest, picked by weirdness (as Minecraft does for its variants).
        if (biome == Biome.DarkForest && c.Weirdness > 0.3f) return Biome.PaleGarden;
        return biome;
    }
}
