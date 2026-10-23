// Voxel terrain shader.
//
//   Technique "Main"   - draws chunk meshes with baked voxel light + sun/moon + shadows + fog + underwater caustics.
//                        Alpha holds the distance from the camera, which the water pass uses to measure water depth.
//   Technique "Shadow" - draws chunk meshes from the sun's point of view into the shadow map (depth only).
//
// Per-vertex data (all 0..1):
//   COLOR0: R = sky light level   G = block light level   B = ambient occlusion   A = emissive (lamp faces)
//   COLOR1: R = depth under water (blocks / 15; 0 = not underwater)   G = sky light at the water's surface

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 ViewProjection;
float4x4 LightViewProjection;

float3 CameraPosition;
float3 LightDirection;      // unit vector pointing TOWARD the sun (or moon at night)
float3 LightColor;          // direct light colour * intensity; black means no direct light
float3 AmbientColor;        // sky ambient colour for the current time of day
float3 BlockLightColor;     // tint of lamp light
float3 FogColor;
float FogStart;
float FogEnd;
float Time;                 // seconds, for animating caustics

float ShadowMapSize;
float ShadowBias;           // in normalised depth units
float ShadowNormalOffset;   // in world units

Texture2D BlockTexture;
sampler2D BlockSampler = sampler_state
{
    Texture = <BlockTexture>;
    MinFilter = Point; MagFilter = Point; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};

Texture2D ShadowMap;
sampler2D ShadowSampler = sampler_state
{
    Texture = <ShadowMap>;
    MinFilter = Point; MagFilter = Point; MipFilter = Point;
    AddressU = Clamp; AddressV = Clamp;
};

Texture2D NormalMap;        // tiling water normal map; its wobble is what caustics are made from
sampler2D NormalSampler = sampler_state
{
    Texture = <NormalMap>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
    AddressU = Wrap; AddressV = Wrap;
};

struct VSInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 TexCoord : TEXCOORD0;
    float4 Light    : COLOR0;
    float4 Extra    : COLOR1;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
    float4 Light    : TEXCOORD1;
    float3 Normal   : TEXCOORD2;
    float3 WorldPos : TEXCOORD3;
    float4 Extra    : TEXCOORD4;
};

// ---------------------------------------------------------------- Main pass

VSOutput MainVS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, ViewProjection);
    output.TexCoord = input.TexCoord;
    output.Light = input.Light;
    output.Normal = input.Normal;
    output.WorldPos = input.Position.xyz;
    output.Extra = input.Extra;
    return output;
}

// Light falloff: each level is 85% as bright as the one above it (Minecraft uses 80%; this is a little
// gentler so shaded areas, like forest floors, stay readable).
float LightCurve(float level)
{
    float floorValue = pow(0.85, 15.0);
    float v = pow(0.85, 15.0 * (1.0 - level));
    return (v - floorValue) / (1.0 - floorValue);
}

// 1 = fully lit, 0 = in shadow. 3x3 percentage-closer filtering softens the edges.
float ShadowFactor(float3 worldPos, float3 normal, float ndl)
{
    float3 p = worldPos + normal * ShadowNormalOffset;
    float4 lp = mul(float4(p, 1.0), LightViewProjection);
    float2 uv = lp.xy / lp.w * float2(0.5, -0.5) + 0.5;
    float depth = lp.z / lp.w - ShadowBias * (1.0 + 3.0 * (1.0 - ndl));

    float lit = 0.0;
    for (int x = -1; x <= 1; x++)
    {
        for (int y = -1; y <= 1; y++)
        {
            float stored = tex2D(ShadowSampler, uv + float2(x, y) / ShadowMapSize).r;
            lit += (depth <= stored) ? 1.0 : 0.0;
        }
    }
    lit /= 9.0;

    // Outside the shadow map's area there is no shadow information: treat as lit.
    float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
    return lerp(1.0, lit, inside);
}

// Light focused by the rippling water surface above: bright, moving networks of lines on the sea floor.
// The pack-style trick: the divergence of a scrolling normal map (how much the slopes converge) is where
// light bunches up. Two layers at different scales and speeds make it look like it is never repeating.
float Caustics(float3 worldPos, float3 normal)
{
    float2 plane = abs(normal.y) > 0.5 ? worldPos.xz : (abs(normal.x) > 0.5 ? worldPos.zy : worldPos.xy);

    float2 uvA = plane * 0.12 + float2(Time * 0.020, Time * 0.013);
    float2 uvB = plane * 0.07 - float2(Time * 0.014, Time * -0.019);
    float e = 0.012;

    float divA = (tex2D(NormalSampler, uvA + float2(e, 0)).r - tex2D(NormalSampler, uvA - float2(e, 0)).r)
               + (tex2D(NormalSampler, uvA + float2(0, e)).g - tex2D(NormalSampler, uvA - float2(0, e)).g);
    float divB = (tex2D(NormalSampler, uvB + float2(e, 0)).r - tex2D(NormalSampler, uvB - float2(e, 0)).r)
               + (tex2D(NormalSampler, uvB + float2(0, e)).g - tex2D(NormalSampler, uvB - float2(0, e)).g);

    float c = saturate(0.35 + (divA + divB) * 3.2);
    return c * c * 2.2;
}

float4 MainPS(VSOutput input) : COLOR0
{
    float4 tex = tex2D(BlockSampler, input.TexCoord);

    float sky = input.Light.r;
    float blk = input.Light.g;
    float ao = input.Light.b;
    float emissive = input.Light.a;

    float3 N = normalize(input.Normal);

    // Faces under water: the sky light baked into the mesh was dimmed block by block, which would make anything
    // deeper than a few blocks pitch black. Instead fade the light at the surface smoothly with depth.
    float waterDepth = input.Extra.r * 15.0;
    float surfaceSky = input.Extra.g;
    float submerged = step(0.5, waterDepth);
    float depthFade = exp(-waterDepth * 0.09);
    float skyLevel = lerp(sky, surfaceSky * depthFade, submerged);

    // Ambient sky light (flood-filled through the world, so caves and overhangs are dark).
    float hemisphere = lerp(0.7, 1.0, N.y * 0.5 + 0.5);
    float3 ambient = AmbientColor * LightCurve(skyLevel) * hemisphere;

    // Lamps.
    float3 lamps = BlockLightColor * LightCurve(blk);

    // Direct sun / moon light, cut by the shadow map and by the voxel sky exposure.
    float ndl = saturate(dot(N, LightDirection));
    float exposure = lerp(smoothstep(0.55, 0.9, sky), smoothstep(0.55, 0.9, surfaceSky) * exp(-waterDepth * 0.14), submerged);
    float shadow = ShadowFactor(input.WorldPos, N, ndl);
    float3 direct = LightColor * ndl * shadow * exposure;

    // Caustics dance across lit surfaces under water, fading out with depth.
    direct += LightColor * ndl * shadow * submerged * Caustics(input.WorldPos, N) * exp(-waterDepth * 0.2) * smoothstep(0.55, 0.9, surfaceSky);

    float3 light = (ambient + lamps + direct) * ao;
    light = lerp(light, float3(1.0, 1.0, 1.0), emissive);

    float3 color = tex.rgb * light;

    float dist = distance(input.WorldPos, CameraPosition);
    float fog = saturate((dist - FogStart) / (FogEnd - FogStart));
    // Alpha carries the distance so the water pass can tell how far a ray travels through water.
    return float4(lerp(color, FogColor, fog), dist);
}

technique Main
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}

// -------------------------------------------------------------- Shadow pass

struct ShadowVSOutput
{
    float4 Position : SV_POSITION;
    float Depth     : TEXCOORD0;
};

ShadowVSOutput ShadowVS(VSInput input)
{
    ShadowVSOutput output;
    output.Position = mul(input.Position, LightViewProjection);
    output.Depth = output.Position.z / output.Position.w;
    return output;
}

float4 ShadowPS(ShadowVSOutput input) : COLOR0
{
    return float4(input.Depth, 0, 0, 1);
}

technique Shadow
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL ShadowVS();
        PixelShader = compile PS_SHADERMODEL ShadowPS();
    }
}
