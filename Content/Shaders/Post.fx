// Full-screen post-processing passes. Each technique is one step of an effect (see src/Rendering/PostProcess/):
//
//   Bloom:     BloomPrefilter -> BloomDownsample (several times) -> BloomUpsample (back up) -> BloomComposite
//   Vignette:  one pass, darkens the corners
//   Grade:     one pass, exposure / contrast / saturation
//   GodRays:   one pass at half resolution (light shafts), then BloomComposite adds them to the scene
//   AgX:       one pass, squeezes the unclamped scene into a displayable range with a filmic curve
//
// The scene arrives as a half-float texture, so values above 1 (the sun, lamps, glints on water) are still there to
// be found by the bloom threshold. Its alpha holds a distance, not opacity, so every pass writes alpha = 1.

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float2 TexelSize;        // 1 / size of SourceTex

// Bloom
float Threshold;         // brightness above which things glow
float Knee;              // softness of the threshold (0 = hard edge)
float Intensity;         // how much glow is added back
float Scatter;           // how much of each wider blur is mixed in on the way back up (0..1)

// Vignette
float VignetteStrength;  // 0 = none, 1 = corners go black
float VignetteRadius;    // distance from the centre (0 = centre, ~1.4 = corner) where darkening starts
float VignetteSoftness;  // how far past the radius the darkening takes to reach full strength
float Aspect;            // width / height

// God rays
float2 SunUv;            // where the sun and moon are on screen, in texture coordinates (may be off the edge)
float2 MoonUv;
float SunStrength;       // 0 = not shining (below the horizon, behind us)
float MoonStrength;
float RayThreshold;      // brightness above which sky counts as a light source (the sun and moon are drawn brighter than white)
float RayDecay;          // how much each step toward the light dims what it carries (just under 1)

// AgX
float TonemapExposure;
float Punchy;            // 0 = AgX's own flat look, 1 = a lot more contrast and colour

// Grade
float Exposure;
float Contrast;
float Saturation;

Texture2D SourceTex;
sampler2D SourceSampler = sampler_state
{
    Texture = <SourceTex>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};

Texture2D OtherTex;      // the bloom to add, or the same-sized level to add to on the way up
sampler2D OtherSampler = sampler_state
{
    Texture = <OtherTex>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};

struct VSInput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VSOutput FullscreenVS(VSInput input)
{
    VSOutput output;
    output.Position = float4(input.Position.xy, 0.0, 1.0);
    output.TexCoord = input.TexCoord;
    return output;
}

// ---------------------------------------------------------------- bloom

// Keeps the part of a colour that is brighter than the threshold, with a soft shoulder instead of a hard cut.
float3 Bright(float3 c)
{
    float brightness = max(c.r, max(c.g, c.b));
    float soft = clamp(brightness - Threshold + Knee, 0.0, 2.0 * Knee);
    soft = soft * soft / (4.0 * Knee + 1e-4);
    float contribution = max(soft, brightness - Threshold) / max(brightness, 1e-4);
    return c * contribution;
}

float4 BloomPrefilterPS(VSOutput input) : COLOR0
{
    // Threshold each sample before averaging, so one very bright pixel can't flicker across a whole block.
    float2 uv = input.TexCoord;
    float3 sum = Bright(tex2D(SourceSampler, uv).rgb) * 4.0;
    sum += Bright(tex2D(SourceSampler, uv + float2(-1, -1) * TexelSize).rgb);
    sum += Bright(tex2D(SourceSampler, uv + float2( 1, -1) * TexelSize).rgb);
    sum += Bright(tex2D(SourceSampler, uv + float2(-1,  1) * TexelSize).rgb);
    sum += Bright(tex2D(SourceSampler, uv + float2( 1,  1) * TexelSize).rgb);
    return float4(sum / 8.0, 1.0);
}

float4 BloomDownsamplePS(VSOutput input) : COLOR0
{
    float2 uv = input.TexCoord;
    float3 sum = tex2D(SourceSampler, uv).rgb * 4.0;
    sum += tex2D(SourceSampler, uv + float2(-1, -1) * TexelSize).rgb;
    sum += tex2D(SourceSampler, uv + float2( 1, -1) * TexelSize).rgb;
    sum += tex2D(SourceSampler, uv + float2(-1,  1) * TexelSize).rgb;
    sum += tex2D(SourceSampler, uv + float2( 1,  1) * TexelSize).rgb;
    return float4(sum / 8.0, 1.0);
}

float4 BloomUpsamplePS(VSOutput input) : COLOR0
{
    // A 3x3 tent filter over the smaller (blurrier) image, added to the same-sized level from the way down.
    float2 uv = input.TexCoord;
    float3 low = tex2D(SourceSampler, uv).rgb * 4.0;
    low += tex2D(SourceSampler, uv + float2(-1,  0) * TexelSize).rgb * 2.0;
    low += tex2D(SourceSampler, uv + float2( 1,  0) * TexelSize).rgb * 2.0;
    low += tex2D(SourceSampler, uv + float2( 0, -1) * TexelSize).rgb * 2.0;
    low += tex2D(SourceSampler, uv + float2( 0,  1) * TexelSize).rgb * 2.0;
    low += tex2D(SourceSampler, uv + float2(-1, -1) * TexelSize).rgb;
    low += tex2D(SourceSampler, uv + float2( 1, -1) * TexelSize).rgb;
    low += tex2D(SourceSampler, uv + float2(-1,  1) * TexelSize).rgb;
    low += tex2D(SourceSampler, uv + float2( 1,  1) * TexelSize).rgb;
    low /= 16.0;

    float3 current = tex2D(OtherSampler, uv).rgb;
    return float4(current + low * Scatter, 1.0);
}

float4 BloomCompositePS(VSOutput input) : COLOR0
{
    float3 scene = tex2D(SourceSampler, input.TexCoord).rgb;
    float3 glow = tex2D(OtherSampler, input.TexCoord).rgb;
    return float4(scene + glow * Intensity, 1.0);
}

// ---------------------------------------------------------------- vignette

float4 VignettePS(VSOutput input) : COLOR0
{
    float3 color = tex2D(SourceSampler, input.TexCoord).rgb;

    // Distance from the centre, with the shorter side of the screen counting as 1 so the shape is a circle, not an oval.
    float2 p = (input.TexCoord - 0.5) * 2.0;
    p.x *= Aspect;
    float edge = smoothstep(VignetteRadius, VignetteRadius + VignetteSoftness, length(p));
    return float4(color * (1.0 - edge * VignetteStrength), 1.0);
}

// ---------------------------------------------------------------- colour grade

float4 GradePS(VSOutput input) : COLOR0
{
    float3 color = tex2D(SourceSampler, input.TexCoord).rgb * Exposure;
    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
    color = lerp(float3(luma, luma, luma), color, Saturation);
    color = (color - 0.5) * Contrast + 0.5;
    return float4(max(color, 0.0), 1.0);
}

// ---------------------------------------------------------------- god rays
//
// Light shafts by radial blur: for every pixel, walk in a straight line toward the sun (or moon) and add up the sky
// light seen along the way. If anything solid (a hill, a tree) lies on that line, the sum stays low, so the pixel is dark;
// where there are gaps, light gets through, which makes shafts fan out from the light around whatever blocks it.
// The scene's alpha holds the distance from the camera, which is huge for sky, so "alpha is big" means "this is sky".
// The light itself is whatever sky is brighter than white: only the sun and moon discs are, as they are drawn boosted.

#define RAY_STEPS 64

float3 Shaft(float2 uv, float2 light, float strength)
{
    if (strength <= 0.0) return float3(0, 0, 0);

    // March from this pixel most of the way to the light. Start each pixel a random fraction of a step in, so the
    // steps of neighbouring pixels don't line up into visible bands.
    float2 stride = (light - uv) * (0.95 / RAY_STEPS);
    float noise = frac(52.9829189 * frac(dot(uv / TexelSize, float2(0.06711056, 0.00583715))));
    float2 p = uv + stride * noise;

    float3 sum = float3(0, 0, 0);
    float weight = 1.0;
    [loop]
    for (int i = 0; i < RAY_STEPS; i++)
    {
        p += stride;
        float4 s = tex2D(SourceSampler, p);
        float brightness = max(s.r, max(s.g, s.b));
        float isSky = step(1000.0, s.a) * step(0.0, p.x) * step(p.x, 1.0) * step(0.0, p.y) * step(p.y, 1.0);
        float3 emitted = s.rgb * (saturate(brightness - RayThreshold) / max(brightness, 1e-4));
        sum += emitted * (isSky * weight);
        weight *= RayDecay;
    }
    return sum / RAY_STEPS * strength * 2.2;
}

float4 GodRaysPS(VSOutput input) : COLOR0
{
    float3 shafts = Shaft(input.TexCoord, SunUv, SunStrength) + Shaft(input.TexCoord, MoonUv, MoonStrength);
    return float4(shafts, 1.0);
}

// ---------------------------------------------------------------- AgX tone mapping
//
// AgX (Troy Sobotka) turns scene brightness, which can be anything from 0 upwards, into a picture that keeps detail in
// both shadows and highlights, and lets very bright saturated colours roll off toward white instead of clipping to a
// flat hue. Steps: move into a wide colour space, pull the primaries slightly inward ("inset"), take the log of the
// brightness over a fixed range of stops, run an S-shaped contrast curve, then undo the inset. This is the widely used
// polynomial version of the curve.
//
// Matrices are written one row per line and applied as mul(matrix, colour).

static const float3x3 SrgbToRec2020 = float3x3(
    0.6274, 0.3293, 0.0433,
    0.0691, 0.9195, 0.0113,
    0.0164, 0.0880, 0.8956);

static const float3x3 Rec2020ToSrgb = float3x3(
     1.6605, -0.5876, -0.0728,
    -0.1246,  1.1329, -0.0083,
    -0.0182, -0.1006,  1.1187);

static const float3x3 AgxInset = float3x3(
    0.856627153315983,  0.0951212405381588, 0.0482516061458583,
    0.137318972929847,  0.761241990602591,  0.101439036467562,
    0.11189821299995,   0.0767994186031903, 0.811302368396859);

static const float3x3 AgxOutset = float3x3(
     1.1271005818144368,  -0.11060664309660323, -0.016493938717834573,
    -0.1413297634984383,   1.157823702216272,   -0.016493938717834257,
    -0.14132976349843826, -0.11060664309660294,  1.2519364065950405);

static const float AgxMinEv = -12.47393;   // darkest and brightest stops (powers of two relative to mid grey) the curve covers
static const float AgxMaxEv = 4.026069;

// The S-curve, as a 6th-order polynomial fitted to AgX's lookup table.
float3 AgxContrast(float3 x)
{
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

float4 AgxPS(VSOutput input) : COLOR0
{
    // The game shades in display values (0..1 as shown on screen), but AgX expects linear light, where mid grey is 0.18.
    // Feeding display values in directly lifts every mid-tone and flattens the picture, so convert first. Anything
    // brighter than white (lamps, the glow from bloom) is simply carried further up the curve.
    float3 color = pow(max(tex2D(SourceSampler, input.TexCoord).rgb, 0.0), 2.2) * TonemapExposure;

    color = mul(SrgbToRec2020, color);
    color = mul(AgxInset, color);
    color = max(color, 1e-10);
    color = (log2(color) - AgxMinEv) / (AgxMaxEv - AgxMinEv);
    color = saturate(color);
    color = AgxContrast(color);

    // AgX on its own is deliberately flat. This adds back some contrast and colour, applied to the curve's output.
    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
    float3 punchy = pow(max(color, 0.0), 1.35);
    punchy = luma + 1.4 * (punchy - luma);
    color = lerp(color, punchy, Punchy);

    color = mul(AgxOutset, color);
    // The curve's output is display-encoded (gamma 2.2) and the screen expects the same, so go to linear only to
    // change colour space and straight back again.
    color = pow(max(color, 0.0), 2.2);
    color = mul(Rec2020ToSrgb, color);
    color = pow(saturate(color), 1.0 / 2.2);
    return float4(color, 1.0);
}

// ---------------------------------------------------------------- techniques

technique BloomPrefilter
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FullscreenVS();
        PixelShader = compile PS_SHADERMODEL BloomPrefilterPS();
    }
}

technique BloomDownsample
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FullscreenVS();
        PixelShader = compile PS_SHADERMODEL BloomDownsamplePS();
    }
}

technique BloomUpsample
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FullscreenVS();
        PixelShader = compile PS_SHADERMODEL BloomUpsamplePS();
    }
}

technique BloomComposite
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FullscreenVS();
        PixelShader = compile PS_SHADERMODEL BloomCompositePS();
    }
}

technique Vignette
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FullscreenVS();
        PixelShader = compile PS_SHADERMODEL VignettePS();
    }
}

technique GodRays
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FullscreenVS();
        PixelShader = compile PS_SHADERMODEL GodRaysPS();
    }
}

technique AgX
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FullscreenVS();
        PixelShader = compile PS_SHADERMODEL AgxPS();
    }
}

technique Grade
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FullscreenVS();
        PixelShader = compile PS_SHADERMODEL GradePS();
    }
}
