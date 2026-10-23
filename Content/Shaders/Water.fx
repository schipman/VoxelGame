// Water surface shader.
//
// The world is drawn first into SceneTexture (colour in RGB, distance from the camera in alpha). Water is drawn
// afterwards, and each water pixel reads a copy of that scene to work out what it is looking at:
//
//   Waves       the vertex shader raises the surface with a few travelling sine waves; the pixel shader uses the
//               same waves' slopes so lighting matches the geometry.
//   Normal maps a tiling normal map is read at three scales scrolling in different directions, for small ripples.
//               Rivers scroll along their flow direction; the sea drifts with the wind.
//   Ripples     a separate simulation (Ripples.fx) leaves rings and wakes around anything that moves through the
//               water; their slopes are added to the normal.
//   Refraction  the scene is sampled at an offset along the water normal. Samples that land on something nearer
//               than the water are rejected (otherwise objects in front of the water would smear into it).
//   Absorption  light fades with the distance it travels through water (red first), tinting the seabed blue-green
//               and fading into deep colour. The distance comes from the scene texture's alpha channel.
//   Fresnel     grazing angles reflect the sky, steep angles let you see through.
//   Foam        a frothy edge where the water is very shallow.

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 ViewProjection;
float3 CameraPosition;
float Time;

float3 LightDirection;      // toward the sun (or moon)
float3 LightColor;
float3 AmbientColor;
float3 BlockLightColor;
float3 FogColor;
float FogStart;
float FogEnd;
float3 SkyZenith;           // colour straight up; FogColor doubles as the horizon colour

float3 ShallowColor;        // what the water scatters in the shallows
float3 DeepColor;           // ... and far from any shore
float WaveAmplitude;        // height of the waves, in blocks
float2 WindDirection;
float RefractionStrength;

float RippleWorldSize;      // how many blocks the ripple map covers before it wraps
float RippleTexel;          // 1 / ripple map resolution
float RippleStrength;
float3 PlayerPosition;      // ripples fade out away from the player (the map wraps around)
float RippleFadeStart;
float RippleFadeEnd;

Texture2D SceneTexture;
sampler2D SceneSampler = sampler_state
{
    Texture = <SceneTexture>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
    AddressU = Clamp; AddressV = Clamp;
};

Texture2D NormalMap;
sampler2D NormalSampler = sampler_state
{
    Texture = <NormalMap>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
    AddressU = Wrap; AddressV = Wrap;
};

Texture2D RippleMap;
sampler2D RippleSampler = sampler_state
{
    Texture = <RippleMap>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
    AddressU = Wrap; AddressV = Wrap;
};

struct VSInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 TexCoord : TEXCOORD0;
    float4 Light    : COLOR0;
    float4 Extra    : COLOR1;   // R = on the surface (waves move it), G,B = flow direction
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float3 WorldPos : TEXCOORD0;
    float3 Normal   : TEXCOORD1;
    float4 Light    : TEXCOORD2;
    float2 Flow     : TEXCOORD3;
    float4 ClipPos  : TEXCOORD4;
};

// One travelling wave: returns (height, slope in x, slope in z).
float3 Wave(float2 p, float2 dir, float frequency, float speed, float amplitude)
{
    float phase = dot(p, dir) * frequency + Time * speed;
    float s = sin(phase);
    float c = cos(phase);
    return float3(s * amplitude, c * amplitude * frequency * dir.x, c * amplitude * frequency * dir.y);
}

// A few waves at different angles, wavelengths and speeds, so the surface never looks like one repeating pattern.
float3 Waves(float2 p)
{
    float3 w = Wave(p, normalize(float2(1.0, 0.35)),   0.65, 1.1, 1.00);
    w += Wave(p, normalize(float2(-0.45, 1.0)),        1.15, 1.5, 0.55);
    w += Wave(p, normalize(float2(0.8, 0.9)),          1.90, 2.1, 0.30);
    w += Wave(p, normalize(float2(-1.0, -0.25)),       3.20, 2.9, 0.16);
    return w * WaveAmplitude;
}

VSOutput WaterVS(VSInput input)
{
    VSOutput output;
    float3 p = input.Position.xyz;
    p.y += Waves(p.xz).x * input.Extra.r;   // only surface vertices move

    output.Position = mul(float4(p, 1.0), ViewProjection);
    output.ClipPos = output.Position;
    output.WorldPos = p;
    output.Normal = input.Normal;
    output.Light = input.Light;
    output.Flow = input.Extra.gb * 2.0 - 1.0;
    return output;
}

float LightCurve(float level)
{
    float floorValue = pow(0.85, 15.0);
    float v = pow(0.85, 15.0 * (1.0 - level));
    return (v - floorValue) / (1.0 - floorValue);
}

float2 DecodeSlope(float4 texel)
{
    return texel.xy * 2.0 - 1.0;
}

float4 WaterPS(VSOutput input) : COLOR0
{
    float3 wp = input.WorldPos;
    float3 geometricNormal = normalize(input.Normal);
    float3 toCamera = CameraPosition - wp;
    float dist = length(toCamera);
    float3 V = toCamera / dist;

    float horizontal = step(0.7, abs(geometricNormal.y));

    // ---- Surface normal: waves + scrolling detail + ripples ----
    float2 slope = Waves(wp.xz).yz;

    float flowAmount = saturate(length(input.Flow));
    float2 flowDir = input.Flow / max(length(input.Flow), 0.001);
    float2 drift = lerp(WindDirection * 0.35, flowDir * 1.5, flowAmount);   // how the detail slides, roughly blocks/second

    float2 medium = DecodeSlope(tex2D(NormalSampler, wp.xz * 0.10 - drift * Time * 0.05));
    float2 small  = DecodeSlope(tex2D(NormalSampler, wp.xz * 0.33 - (drift * 1.6 + float2(0.1, -0.2)) * Time * 0.05));
    float2 big    = DecodeSlope(tex2D(NormalSampler, wp.xz * 0.035 - drift * 0.6 * Time * 0.05));
    slope += (medium * 0.55 + small * 0.30 + big * 0.45) * 0.30;

    float2 rippleUV = wp.xz / RippleWorldSize;
    float rippleCentre = tex2D(RippleSampler, rippleUV).r;
    float rippleX = tex2D(RippleSampler, rippleUV + float2(RippleTexel, 0)).r;
    float rippleZ = tex2D(RippleSampler, rippleUV + float2(0, RippleTexel)).r;
    float rippleFade = 1.0 - smoothstep(RippleFadeStart, RippleFadeEnd, distance(wp.xz, PlayerPosition.xz));
    slope += float2(rippleX - rippleCentre, rippleZ - rippleCentre) * RippleStrength * rippleFade;

    // Flat water faces up (or down, seen from underneath); side walls keep their geometric normal.
    float3 waveNormal = normalize(float3(-slope.x, 1.0, -slope.y)) * (geometricNormal.y >= 0.0 ? 1.0 : -1.0);
    float3 N = lerp(geometricNormal, waveNormal, horizontal);
    float facing = dot(N, V) >= 0.0 ? 1.0 : -1.0;
    N *= facing;

    // True when the eye is under a top surface, looking up at it.
    float below = horizontal * step(0.0, -V.y) * step(0.0, geometricNormal.y);

    float NdotV = saturate(dot(N, V));
    float fresnel = 0.04 + 0.96 * pow(1.0 - NdotV, 4.0);
    fresnel = lerp(fresnel, saturate(fresnel * 3.0 + 0.1), below);

    // ---- What is behind the water ----
    float2 screenUV = input.ClipPos.xy / input.ClipPos.w * float2(0.5, -0.5) + 0.5;
    float4 straight = tex2D(SceneSampler, screenUV);
    float straightThickness = max(straight.a - dist, 0.0);

    // Bend the view along the normal, more for closer water, and only when there's something behind it to bend toward.
    float2 offset = N.xz * RefractionStrength / (3.0 + dist) * saturate(straightThickness * 1.5);
    float4 bent = tex2D(SceneSampler, screenUV + offset);
    float behind = step(dist, bent.a);
    float4 scene = lerp(straight, bent, behind);

    float thickness = min(max(scene.a - dist, 0.0), 48.0) * (1.0 - below);

    // ---- Lighting of the water itself ----
    float sky = input.Light.r;
    float3 lamps = BlockLightColor * LightCurve(input.Light.g);
    float3 sunlight = LightColor * smoothstep(0.55, 0.9, sky);
    float3 waterLight = AmbientColor * LightCurve(sky) * 1.5 + sunlight * 0.5 + lamps;

    // ---- Absorption: red goes first, so the bottom turns blue-green, then dark ----
    float3 absorption = float3(0.42, 0.095, 0.055);
    float3 transmittance = exp(-absorption * thickness);
    float3 scatter = lerp(ShallowColor, DeepColor, saturate(thickness / 16.0)) * waterLight;
    float3 refracted = scene.rgb * transmittance + scatter * (1.0 - transmittance);
    refracted = lerp(refracted, scene.rgb * float3(0.75, 0.95, 1.0), below);   // looking up through the surface

    // ---- Reflection: sky gradient plus a sharp sun glint ----
    float3 R = reflect(-V, N);
    float3 skyColor = lerp(FogColor, SkyZenith, saturate(R.y * 1.5));
    skyColor *= lerp(0.18, 1.0, smoothstep(0.25, 0.9, sky));   // under a roof there's little sky to reflect
    skyColor = lerp(skyColor, scatter, below);
    float sunDot = saturate(dot(R, LightDirection));
    float3 glint = LightColor * (pow(sunDot, 500.0) * 7.0 + pow(sunDot, 40.0) * 0.2) * smoothstep(0.4, 0.9, sky) * (1.0 - below);

    float3 color = lerp(refracted, skyColor, fresnel) + glint * (0.35 + 0.65 * fresnel);

    // ---- Shoreline foam ----
    float shore = saturate(1.0 - thickness / 1.3) * horizontal * (1.0 - below);
    float pattern = tex2D(NormalSampler, wp.xz * 0.8 + float2(Time * 0.02, -Time * 0.015)).b;
    float foam = smoothstep(0.45, 0.75, pattern * 0.7 + shore * 0.55) * shore;
    color = lerp(color, float3(0.92, 0.96, 1.0) * (waterLight * 0.6 + 0.25), foam * 0.85);

    // ---- Wake foam: where ripples are tall (a swimmer's wake, a splash, raindrops) the surface turns white-capped ----
    float wakeFoam = smoothstep(0.05, 0.22, abs(rippleCentre)) * rippleFade * horizontal * (1.0 - below);
    wakeFoam *= 0.5 + 0.5 * tex2D(NormalSampler, wp.xz * 1.3 - float2(Time * 0.03, 0.0)).b;
    color = lerp(color, float3(0.92, 0.96, 1.0) * (waterLight * 0.6 + 0.3), wakeFoam * 0.7);

    // ---- Falling water: streaks of foam sliding down vertical faces (waterfalls and the walls of flowing water) ----
    float2 fallUV = float2(wp.x + wp.z, 0.0) * 0.35 + float2(0.0, wp.y * 0.22 + Time * 0.9);
    float streak = tex2D(NormalSampler, fallUV).b;
    float streaks = smoothstep(0.55, 0.8, streak) * (1.0 - horizontal);
    color = lerp(color, float3(0.92, 0.96, 1.0) * (waterLight * 0.7 + 0.3), streaks * 0.7);

    float fog = saturate((dist - FogStart) / (FogEnd - FogStart));
    return float4(lerp(color, FogColor, fog), dist);
}

technique Water
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL WaterVS();
        PixelShader = compile PS_SHADERMODEL WaterPS();
    }
}
