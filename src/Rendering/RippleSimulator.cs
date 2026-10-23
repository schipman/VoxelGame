using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;

namespace VoxelGame.Rendering;

/// <summary>
/// Ripples on the water: a wave simulation (Ripples.fx) that runs on two render targets, ping-ponging between them.
/// Anything that moves through water calls <see cref="Disturb"/>; the water shader then reads <see cref="State"/>
/// and bends its normals by the slope of the ripple heights.
///
/// The map covers a square patch of the world and wraps around like a torus, so it never needs to scroll with the
/// player: a point in the world always lands on the same texel, and old rings simply fade away.
/// </summary>
public sealed class RippleSimulator : IDisposable
{
    public const int Resolution = 256;
    public const float WorldSize = 48f;             // blocks covered before the map wraps
    private const float StepSeconds = 1f / 60f;     // the simulation runs at a fixed rate whatever the frame rate
    private const int MaxStepsPerFrame = 3;

    private readonly GraphicsDevice _device;
    private readonly Effect _effect;
    private readonly FullscreenQuad _quad = new();
    private RenderTarget2D _current;
    private RenderTarget2D _next;
    private float _accumulator;

    private readonly record struct Disturbance(Vector2 From, Vector2 To, float Radius, float Strength);
    private readonly List<Disturbance> _pending = new();

    /// <summary>The ripple heights: red = height, green = velocity.</summary>
    public Texture2D State => _current;

    public RippleSimulator(GraphicsDevice device, ContentManager content)
    {
        _device = device;
        _effect = content.Load<Effect>("Shaders/Ripples");
        _current = CreateTarget();
        _next = CreateTarget();

        // Start flat.
        foreach (var target in new[] { _current, _next })
        {
            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);
        }
        device.SetRenderTarget(null);

        _effect.Parameters["TexelSize"].SetValue(new Vector2(1f / Resolution));
        // Wave speed: (c * dt / texel)^2 with c = 6 blocks/second. Must stay below 0.5 to be stable.
        float texelsPerStep = 6f * StepSeconds / (WorldSize / Resolution);
        _effect.Parameters["WaveSpeed"].SetValue(texelsPerStep * texelsPerStep);
        _effect.Parameters["Damping"].SetValue(0.992f);
    }

    private RenderTarget2D CreateTarget() =>
        new(_device, Resolution, Resolution, false, SurfaceFormat.HalfVector4, DepthFormat.None);

    /// <summary>
    /// Stirs the water along a line from one world position to another, e.g. the path a swimmer took this frame.
    /// Strength is roughly the height of the initial bump; radius is in blocks.
    /// </summary>
    public void Disturb(Vector2 fromWorldXZ, Vector2 toWorldXZ, float radius, float strength)
    {
        if (_pending.Count > 8) return;   // a burst of disturbances in one frame isn't worth more
        _pending.Add(new Disturbance(fromWorldXZ / WorldSize, toWorldXZ / WorldSize, radius / WorldSize, strength));
    }

    /// <summary>Advances the simulation. Switches render targets, so call it before the frame's own drawing begins.</summary>
    public void Update(float dt)
    {
        _accumulator = Math.Min(_accumulator + dt, StepSeconds * MaxStepsPerFrame);

        while (_accumulator >= StepSeconds)
        {
            _accumulator -= StepSeconds;
            Step();
        }
    }

    private void Step()
    {
        // One reported disturbance is applied per step, oldest first.
        var impulse = _pending.Count > 0 ? _pending[0] : default;
        if (_pending.Count > 0) _pending.RemoveAt(0);

        _device.SetRenderTarget(_next);
        _device.BlendState = BlendState.Opaque;
        _device.DepthStencilState = DepthStencilState.None;
        _device.RasterizerState = RasterizerState.CullNone;

        _effect.Parameters["State"].SetValue(_current);
        _effect.Parameters["ImpulseFrom"].SetValue(impulse.From);
        _effect.Parameters["ImpulseTo"].SetValue(impulse.To);
        _effect.Parameters["ImpulseRadius"].SetValue(Math.Max(impulse.Radius, 0.001f));
        _effect.Parameters["ImpulseStrength"].SetValue(impulse.Strength);

        _effect.CurrentTechnique.Passes[0].Apply();
        _quad.Draw(_device);
        _device.SetRenderTarget(null);

        (_current, _next) = (_next, _current);
    }

    /// <summary>Reads the ripple heights back from the GPU: (min, max, mean absolute). Slow; for debugging and tests only.</summary>
    public (float Min, float Max, float MeanAbs) ReadStats()
    {
        var data = new HalfVector4[Resolution * Resolution];
        _current.GetData(data);
        float min = float.MaxValue, max = float.MinValue, sum = 0;
        foreach (var texel in data)
        {
            float h = texel.ToVector4().X;
            min = Math.Min(min, h); max = Math.Max(max, h); sum += MathF.Abs(h);
        }
        return (min, max, sum / data.Length);
    }

    public void Dispose()
    {
        _current.Dispose();
        _next.Dispose();
    }
}
