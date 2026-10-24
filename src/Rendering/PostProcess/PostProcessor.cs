using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace VoxelGame.Rendering.PostProcess;

/// <summary>
/// What an effect needs while it runs: the device, the shared shader (Post.fx) and a way to run one of its passes over
/// the whole screen.
/// </summary>
public sealed class PostContext
{
    private readonly FullscreenQuad _quad = new();

    public GraphicsDevice Device { get; }
    public Effect Shader { get; }

    /// <summary>Size of the picture being processed (the window).</summary>
    public int Width { get; internal set; }
    public int Height { get; internal set; }

    public PostContext(GraphicsDevice device, Effect shader)
    {
        Device = device;
        Shader = shader;
    }

    /// <summary>Sets a shader parameter. Parameters a pass doesn't use are optimised away, so a missing one is not an error.</summary>
    public void Set(string name, float value) => Shader.Parameters[name]?.SetValue(value);
    public void Set(string name, Vector2 value) => Shader.Parameters[name]?.SetValue(value);

    /// <summary>
    /// Runs one technique of Post.fx over every pixel of <paramref name="target"/> (null = the screen), reading
    /// <paramref name="source"/> as SourceTex and, if given, <paramref name="other"/> as OtherTex.
    /// </summary>
    public void Pass(RenderTarget2D target, string technique, Texture2D source, Texture2D other = null)
    {
        Device.SetRenderTarget(target);
        Device.BlendState = BlendState.Opaque;
        Device.DepthStencilState = DepthStencilState.None;
        Device.RasterizerState = RasterizerState.CullNone;

        Shader.CurrentTechnique = Shader.Techniques[technique];
        Shader.Parameters["SourceTex"]?.SetValue(source);
        Shader.Parameters["OtherTex"]?.SetValue(other);
        Shader.Parameters["TexelSize"]?.SetValue(new Vector2(1f / source.Width, 1f / source.Height));
        Shader.CurrentTechnique.Passes[0].Apply();
        _quad.Draw(Device);
    }
}

/// <summary>
/// One full-screen effect in the stack. It reads the picture so far and writes a changed copy.
/// To add an effect: subclass this, add a technique to Post.fx (or a new .fx), and add an instance to
/// <see cref="PostProcessor.Effects"/> in the order you want it applied.
/// </summary>
public abstract class PostEffect : IDisposable
{
    public bool Enabled = true;

    /// <summary>False when the effect would change nothing (so it costs nothing).</summary>
    public virtual bool IsActive => Enabled;

    /// <param name="source">The picture so far. Don't write to it.</param>
    /// <param name="destination">Where to put the result; null means the screen.</param>
    public abstract void Apply(PostContext context, Texture2D source, RenderTarget2D destination);

    public virtual void Dispose() { }
}

/// <summary>
/// Runs the finished scene through a chain of <see cref="PostEffect"/>s and puts the result on the screen. Effects pass the
/// picture along through two half-float buffers; with nothing active, the scene is just copied across.
/// </summary>
public sealed class PostProcessor : IDisposable
{
    private readonly PostContext _context;
    private readonly SpriteBatch _blit;
    private readonly RenderTarget2D[] _buffers = new RenderTarget2D[2];
    private readonly List<PostEffect> _active = new();

    public GodRaysEffect GodRays { get; } = new();
    public BloomEffect Bloom { get; } = new();
    public AgxTonemapEffect Tonemap { get; } = new();
    public ColorGradeEffect Grade { get; } = new();
    public VignetteEffect Vignette { get; } = new();

    /// <summary>True when an effect would use brightness beyond white, so the scene should draw bright things brighter than 1 (otherwise that would only clip).</summary>
    public bool WantsHdr => Bloom.IsActive || Tonemap.IsActive || GodRays.Wanted;

    /// <summary>The stack, applied first to last. God rays come first because they read the scene's distance channel (every later step overwrites it); bloom needs the unclamped brightness, then the tone mapper squeezes that into a displayable range and grading works on the result; the vignette is last so it also darkens the glow.</summary>
    public List<PostEffect> Effects { get; }

    public PostProcessor(GraphicsDevice device, ContentManager content)
    {
        _context = new PostContext(device, content.Load<Effect>("Shaders/Post"));
        _blit = new SpriteBatch(device);
        Effects = new List<PostEffect> { GodRays, Bloom, Tonemap, Grade, Vignette };
    }

    /// <summary>Applies the stack to the scene and draws the result to the screen.</summary>
    public void Run(Texture2D scene)
    {
        var device = _context.Device;
        var bounds = device.PresentationParameters.Bounds;
        _context.Width = bounds.Width;
        _context.Height = bounds.Height;

        _active.Clear();
        foreach (var effect in Effects)
            if (effect.IsActive) _active.Add(effect);

        if (_active.Count == 0)
        {
            device.SetRenderTarget(null);
            _blit.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            _blit.Draw(scene, bounds, Color.White);
            _blit.End();
            return;
        }

        EnsureBuffers(bounds.Width, bounds.Height);
        Texture2D picture = scene;
        for (int i = 0; i < _active.Count; i++)
        {
            bool last = i == _active.Count - 1;
            var destination = last ? null : _buffers[i % 2];
            _active[i].Apply(_context, picture, destination);
            picture = destination;
        }
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_buffers[0] != null && _buffers[0].Width == width && _buffers[0].Height == height) return;
        foreach (var buffer in _buffers) buffer?.Dispose();
        for (int i = 0; i < _buffers.Length; i++)
            _buffers[i] = new RenderTarget2D(_context.Device, width, height, false, SurfaceFormat.HalfVector4, DepthFormat.None);
    }

    public void Dispose()
    {
        foreach (var buffer in _buffers) buffer?.Dispose();
        foreach (var effect in Effects) effect.Dispose();
        _blit.Dispose();
    }
}
