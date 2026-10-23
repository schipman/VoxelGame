// Ripple simulation: a 2D wave equation run on the GPU.
//
// The ripple map covers a patch of water around the player and wraps around (it is a torus), so it never has to
// scroll. Each pixel stores a height (R) and a velocity (G). Every step, a pixel is pulled toward the average of
// its four neighbours (that is what makes a disturbance spread out as a ring), a little energy is lost to damping,
// and any splash or wake being added this step is stamped in as a soft blob along a line segment.

#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float2 TexelSize;        // 1 / map resolution
float WaveSpeed;         // (speed * dt / texel)^2: how fast rings expand; must stay below 0.5 to be stable
float Damping;           // velocity kept each step, just under 1

float2 ImpulseFrom;      // in map coordinates (0..1, wrapping)
float2 ImpulseTo;
float ImpulseRadius;
float ImpulseStrength;   // 0 = no disturbance this step

Texture2D State;
sampler2D StateSampler = sampler_state
{
    Texture = <State>;
    MinFilter = Point; MagFilter = Point; MipFilter = Point;
    AddressU = Wrap; AddressV = Wrap;
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

VSOutput StepVS(VSInput input)
{
    VSOutput output;
    output.Position = float4(input.Position.xy, 0.0, 1.0);
    output.TexCoord = input.TexCoord;
    return output;
}

float4 StepPS(VSOutput input) : COLOR0
{
    float2 uv = input.TexCoord;
    float4 state = tex2D(StateSampler, uv);
    float height = state.r;
    float velocity = state.g;

    float neighbours = tex2D(StateSampler, uv + float2(TexelSize.x, 0)).r
                     + tex2D(StateSampler, uv - float2(TexelSize.x, 0)).r
                     + tex2D(StateSampler, uv + float2(0, TexelSize.y)).r
                     + tex2D(StateSampler, uv - float2(0, TexelSize.y)).r;

    velocity += (neighbours - 4.0 * height) * WaveSpeed;
    velocity *= Damping;
    height += velocity;

    // Distance from this pixel to the segment ImpulseFrom -> ImpulseTo, measured around the wrapped map.
    float2 along = ImpulseTo - ImpulseFrom;
    along -= floor(along + 0.5);
    float2 toPixel = uv - ImpulseFrom;
    toPixel -= floor(toPixel + 0.5);
    float t = saturate(dot(toPixel, along) / max(dot(along, along), 1e-9));
    float2 d = toPixel - along * t;
    d -= floor(d + 0.5);
    height += ImpulseStrength * exp(-dot(d, d) / (ImpulseRadius * ImpulseRadius));

    return float4(clamp(height, -2.0, 2.0), velocity, 0.0, 1.0);
}

technique Step
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL StepVS();
        PixelShader = compile PS_SHADERMODEL StepPS();
    }
}
