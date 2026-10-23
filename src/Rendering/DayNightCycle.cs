using Microsoft.Xna.Framework;

namespace VoxelGame.Rendering;

/// <summary>
/// Time of day and everything derived from it: where the sun is, the colour of the sky and of the
/// light, and how bright the ambient light is. Time runs 0..1: 0 = midnight, 0.25 = sunrise,
/// 0.5 = noon, 0.75 = sunset.
/// </summary>
public sealed class DayNightCycle
{
    public const float DayLengthSeconds = 600f;

    private float _time = 0.35f;

    /// <summary>Time of day in 0..1.</summary>
    public float Time
    {
        get => _time;
        set { _time = value - MathF.Floor(value); Recalculate(); }
    }

    /// <summary>Unit vector from the world toward the sun. The sun rises in +X and sets in -X.</summary>
    public Vector3 SunDirection { get; private set; }

    /// <summary>Direction toward whichever of sun or moon is above the horizon; used for shading and shadows.</summary>
    public Vector3 LightDirection { get; private set; }

    /// <summary>Colour times intensity of the direct light. Near zero around dusk and dawn.</summary>
    public Vector3 LightColor { get; private set; }

    public Vector3 AmbientColor { get; private set; }
    public Vector3 SkyColor { get; private set; }

    /// <summary>0 at night, 1 in full daylight.</summary>
    public float Daylight { get; private set; }

    public DayNightCycle()
    {
        Recalculate();
    }

    public void Update(float dt, float speed = 1f) => Time += dt / DayLengthSeconds * speed;

    public string Clock
    {
        get
        {
            int minutes = (int)(_time * 24 * 60);
            return $"{minutes / 60:D2}:{minutes % 60:D2}";
        }
    }

    private void Recalculate()
    {
        float angle = (_time - 0.25f) * MathHelper.TwoPi;
        var sun = Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));
        float elevation = sun.Y;

        SunDirection = sun;
        LightDirection = elevation >= 0 ? sun : -sun;

        Daylight = Smooth(-0.15f, 0.2f, elevation);
        float dusk = Math.Clamp(1f - MathF.Abs(elevation) / 0.3f, 0f, 1f);

        var daySky = new Vector3(0.53f, 0.75f, 0.92f);
        var nightSky = new Vector3(0.015f, 0.02f, 0.06f);
        var duskSky = new Vector3(0.95f, 0.50f, 0.28f);
        SkyColor = Vector3.Lerp(Vector3.Lerp(nightSky, daySky, Daylight), duskSky, dusk * dusk * 0.7f);

        AmbientColor = Vector3.Lerp(new Vector3(0.07f, 0.09f, 0.17f), new Vector3(0.52f, 0.58f, 0.72f), Daylight);

        // Sun: warm near the horizon, white when high. Moon: dim and blue.
        float sunStrength = Smooth(0f, 0.12f, elevation);
        float moonStrength = Smooth(0f, 0.12f, -elevation);
        var sunColor = Vector3.Lerp(new Vector3(1.0f, 0.55f, 0.30f), new Vector3(1.0f, 0.96f, 0.88f), Smooth(0f, 0.5f, elevation));
        LightColor = sunColor * (0.55f * sunStrength) + new Vector3(0.45f, 0.55f, 0.9f) * (0.12f * moonStrength);
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }
}
