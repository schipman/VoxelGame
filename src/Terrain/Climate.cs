namespace VoxelGame.Terrain;

/// <summary>
/// The climate at one spot, modelled on Minecraft's biome parameters. Every value is roughly -1..1.
///   Continentalness  low = ocean, rising through coast to deep inland
///   Erosion          low = rugged and mountainous, high = flat
///   Weirdness        folded into peaks-and-valleys: where ridges and valleys run
///   Temperature      cold to hot
///   Humidity         dry to wet
/// </summary>
public readonly record struct Climate(
    float Continentalness,
    float Erosion,
    float Weirdness,
    float Temperature,
    float Humidity)
{
    /// <summary>-1 in valleys, 0 on mid slopes, +1 on ridge tops. Minecraft's formula: 1 - |3|weirdness| - 2|.</summary>
    public float PeaksAndValleys => 1f - MathF.Abs(3f * MathF.Abs(Weirdness) - 2f);

    /// <summary>0 (frozen) to 4 (hot), using Minecraft's temperature band edges.</summary>
    public int TemperatureLevel => Temperature < -0.45f ? 0 : Temperature < -0.15f ? 1 : Temperature < 0.2f ? 2 : Temperature < 0.55f ? 3 : 4;

    /// <summary>0 (arid) to 4 (wet), using Minecraft's humidity band edges.</summary>
    public int HumidityLevel => Humidity < -0.35f ? 0 : Humidity < -0.1f ? 1 : Humidity < 0.1f ? 2 : Humidity < 0.3f ? 3 : 4;
}

/// <summary>Turns a seed into smooth climate maps. Each parameter is its own noise field, so they vary independently.</summary>
public sealed class ClimateSampler
{
    // Size of the features of each field, in blocks (roughly the width of one "blob").
    private const float ContinentScale = 1400f;
    private const float ErosionScale = 1100f;
    private const float WeirdnessScale = 850f;
    private const float TemperatureScale = 1500f;
    private const float HumidityScale = 1200f;

    // Layered Perlin noise bunches up around 0; this stretches it so the full -1..1 range gets used,
    // which keeps the biome thresholds (copied from Minecraft) meaningful.
    private const float Gain = 2.1f;

    // Shifts continentalness toward land. With no water in the game yet, ocean basins are just big dry pits,
    // so oceans are kept to a minority of the world.
    private const float LandBias = 0.22f;

    private readonly PerlinNoise _continentalness;
    private readonly PerlinNoise _erosion;
    private readonly PerlinNoise _weirdness;
    private readonly PerlinNoise _temperature;
    private readonly PerlinNoise _humidity;

    public ClimateSampler(int seed)
    {
        // Distinct seeds so the fields don't mirror each other.
        _continentalness = new PerlinNoise(seed * 31 + 1);
        _erosion         = new PerlinNoise(seed * 31 + 2);
        _weirdness       = new PerlinNoise(seed * 31 + 3);
        _temperature     = new PerlinNoise(seed * 31 + 4);
        _humidity        = new PerlinNoise(seed * 31 + 5);
    }

    public Climate Sample(int x, int z) => new(
        Math.Clamp(Field(_continentalness, x, z, ContinentScale, 4) + LandBias, -1f, 1f),
        Field(_erosion,         x, z, ErosionScale, 3),
        Field(_weirdness,       x, z, WeirdnessScale, 3),
        Field(_temperature,     x, z, TemperatureScale, 2, 1.75f),
        Field(_humidity,        x, z, HumidityScale, 2, 1.75f));

    private static float Field(PerlinNoise noise, int x, int z, float scale, int octaves, float gain = Gain) =>
        Math.Clamp(noise.Fractal(x / scale, z / scale, octaves) * gain, -1f, 1f);
}
