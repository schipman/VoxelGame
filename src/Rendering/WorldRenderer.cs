using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Entities;
using VoxelGame.Terrain;

namespace VoxelGame.Rendering;

/// <summary>
/// Draws the world. One frame goes through these stages:
///   1. Prepare       ripple simulation, then the shadow map (chunks drawn from the sun's point of view, depth only).
///   2. BeginScene    clears the scene target (a half-float texture: RGB = colour, A = distance from the camera).
///   3. DrawTerrain   solid blocks, lit, shadowed and fogged. Caustics appear on anything under water.
///   4. DrawWater     the scene is copied, then water is drawn on top, sampling the copy to refract and tint
///                    whatever lies behind each water pixel. This is why the terrain pass writes distance into alpha.
///   5. Present       the finished scene is copied to the screen.
/// Render-target switches discard the back buffer, which is why everything is built off screen and copied at the end.
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    private const int ShadowMapSize = 2048;
    private const float ShadowRange = 144f;       // width of the area (in blocks) covered by the shadow map
    private const float LightDistance = 200f;     // how far back along the sun direction the shadow camera sits
    private const float FarDistance = 4096f;      // "distance" stored for sky pixels

    private readonly GraphicsDevice _device;
    private readonly Effect _terrain;
    private readonly Effect _water;
    private readonly SpriteBatch _blit;
    private readonly RenderTarget2D _shadowMap;
    private Texture2D _atlas;
    private Texture2D _waterNormals;
    private RenderTarget2D _scene;
    private RenderTarget2D _refraction;

    private Matrix _lightViewProjection;
    private float _time;

    public RippleSimulator Ripples { get; }
    public int ChunksDrawn { get; private set; }

    /// <summary>True when the camera is under water: everything gets the murky underwater fog.</summary>
    public bool CameraUnderwater { get; set; }

    public WorldRenderer(GraphicsDevice device, ContentManager content)
    {
        _device = device;
        _atlas = TextureAtlas.Load(device);
        _waterNormals = AssetFiles.LoadTexture(device, "Assets/Textures/water_normal.png");
        _terrain = content.Load<Effect>("Shaders/Voxel");
        _water = content.Load<Effect>("Shaders/Water");
        _blit = new SpriteBatch(device);
        Ripples = new RippleSimulator(device, content);

        // One 32-bit float channel holding the depth of the nearest surface the sun sees.
        _shadowMap = new RenderTarget2D(device, ShadowMapSize, ShadowMapSize, false, SurfaceFormat.Single, DepthFormat.Depth24);
    }

    // ---------------------------------------------------------------- frame

    /// <summary>Stage 1. Call before BeginScene: both switch render targets, which wipes the back buffer.</summary>
    public void Prepare(World world, Camera camera, DayNightCycle sky, float dt)
    {
        _time = (_time + dt) % 3600f;   // wrapped so the shaders' sines keep their precision on long sessions
        Ripples.Update(dt);

        _lightViewProjection = BuildLightMatrix(camera, sky);

        // At dusk and dawn the light is ~0, so skip the shadow pass entirely.
        if (sky.LightColor.LengthSquared() > 1e-4f)
            DrawShadowMap(world);
    }

    /// <summary>Stage 2.</summary>
    public void BeginScene(DayNightCycle sky)
    {
        EnsureTargets();
        _device.SetRenderTarget(_scene);

        var background = CameraUnderwater ? UnderwaterColor(sky) : sky.SkyColor;
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Vector4(background, FarDistance), 1f, 0);
    }

    /// <summary>Stage 3.</summary>
    public void DrawTerrain(World world, Camera camera, DayNightCycle sky)
    {
        var fog = FogFor(world, sky);

        var effect = _terrain;
        effect.CurrentTechnique = effect.Techniques["Main"];
        Set(effect, "ViewProjection", camera.View * camera.Projection);
        Set(effect, "LightViewProjection", _lightViewProjection);
        Set(effect, "CameraPosition", camera.Position);
        Set(effect, "LightDirection", sky.LightDirection);
        Set(effect, "LightColor", sky.LightColor);
        Set(effect, "AmbientColor", sky.AmbientColor);
        Set(effect, "BlockLightColor", BlockLightColor);
        Set(effect, "FogColor", fog.Color);
        Set(effect, "FogStart", fog.Start);
        Set(effect, "FogEnd", fog.End);
        Set(effect, "Time", _time);
        Set(effect, "ShadowMapSize", (float)ShadowMapSize);
        Set(effect, "ShadowBias", ShadowTexel * 1.5f / (LightDistance * 2f));
        Set(effect, "ShadowNormalOffset", ShadowTexel * 1.5f);
        Set(effect, "BlockTexture", _atlas);
        Set(effect, "NormalMap", _waterNormals);
        Set(effect, "ShadowMap", _shadowMap);

        // SpriteBatch changes these, so reset them every frame.
        _device.DepthStencilState = DepthStencilState.Default;
        _device.BlendState = BlendState.Opaque;
        _device.RasterizerState = RasterizerState.CullCounterClockwise;
        _device.SamplerStates[0] = SamplerState.PointClamp;
        _device.SamplerStates[1] = SamplerState.PointClamp;

        ChunksDrawn = 0;
        DrawMeshes(world, new BoundingFrustum(camera.View * camera.Projection), water: false, countDrawn: true);
    }

    /// <summary>Stage 4. Water is skipped entirely if none is in view.</summary>
    public void DrawWater(World world, Camera camera, DayNightCycle sky, Vector3 playerPosition)
    {
        var frustum = new BoundingFrustum(camera.View * camera.Projection);
        if (!world.Chunks.Any(c => c.WaterMesh.IndexCount > 0 && frustum.Intersects(c.Bounds))) return;

        // Copy what has been drawn so far: the water samples this copy while it draws over the original.
        _device.SetRenderTarget(_refraction);
        _blit.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
        _blit.Draw(_scene, new Rectangle(0, 0, _refraction.Width, _refraction.Height), Color.White);
        _blit.End();
        _device.SetRenderTarget(_scene);   // contents (colour and depth) are preserved

        var fog = FogFor(world, sky);
        var effect = _water;
        effect.CurrentTechnique = effect.Techniques["Water"];
        Set(effect, "ViewProjection", camera.View * camera.Projection);
        Set(effect, "CameraPosition", camera.Position);
        Set(effect, "Time", _time);
        Set(effect, "LightDirection", sky.LightDirection);
        Set(effect, "LightColor", sky.LightColor);
        Set(effect, "AmbientColor", sky.AmbientColor);
        Set(effect, "BlockLightColor", BlockLightColor);
        Set(effect, "FogColor", fog.Color);
        Set(effect, "FogStart", fog.Start);
        Set(effect, "FogEnd", fog.End);
        Set(effect, "SkyZenith", Vector3.Lerp(sky.SkyColor, sky.SkyColor * new Vector3(0.55f, 0.7f, 1.05f), 0.85f));
        Set(effect, "ShallowColor", new Vector3(0.20f, 0.60f, 0.62f));
        Set(effect, "DeepColor", new Vector3(0.035f, 0.19f, 0.34f));
        Set(effect, "WaveAmplitude", 0.055f);
        Set(effect, "WindDirection", new Vector2(0.8f, 0.6f));
        Set(effect, "RefractionStrength", 0.8f);
        Set(effect, "RippleWorldSize", RippleSimulator.WorldSize);
        Set(effect, "RippleTexel", 1f / RippleSimulator.Resolution);
        Set(effect, "RippleStrength", 6f);
        Set(effect, "PlayerPosition", playerPosition);
        Set(effect, "RippleFadeStart", 14f);
        Set(effect, "RippleFadeEnd", 22f);
        Set(effect, "SceneTexture", _refraction);
        Set(effect, "NormalMap", _waterNormals);
        Set(effect, "RippleMap", Ripples.State);

        _device.DepthStencilState = DepthStencilState.Default;
        _device.BlendState = BlendState.Opaque;          // the shader blends with the scene itself
        _device.RasterizerState = RasterizerState.CullNone;   // water is seen from both sides

        DrawMeshes(world, frustum, water: true, countDrawn: false);
    }

    /// <summary>Stage 5: copies the finished scene to the screen.</summary>
    public void Present()
    {
        _device.SetRenderTarget(null);
        var bounds = _device.PresentationParameters.Bounds;
        _blit.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
        _blit.Draw(_scene, bounds, Color.White);
        _blit.End();
    }

    // ---------------------------------------------------------------- pieces

    private static readonly Vector3 BlockLightColor = new(1.3f, 1.0f, 0.62f);

    private static float ShadowTexel => ShadowRange / ShadowMapSize;

    private void DrawShadowMap(World world)
    {
        _terrain.Parameters["ShadowMap"]?.SetValue((Texture2D)null);
        Set(_terrain, "LightViewProjection", _lightViewProjection);
        _terrain.CurrentTechnique = _terrain.Techniques["Shadow"];

        _device.SetRenderTarget(_shadowMap);
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.White, 1f, 0);
        _device.DepthStencilState = DepthStencilState.Default;
        _device.BlendState = BlendState.Opaque;
        _device.RasterizerState = RasterizerState.CullCounterClockwise;

        // Water casts no shadow: only the solid mesh is drawn.
        DrawMeshes(world, new BoundingFrustum(_lightViewProjection), water: false, countDrawn: false);
        _device.SetRenderTarget(null);
    }

    private void DrawMeshes(World world, BoundingFrustum frustum, bool water, bool countDrawn)
    {
        var effect = water ? _water : _terrain;
        effect.CurrentTechnique.Passes[0].Apply();
        foreach (var chunk in world.Chunks)
        {
            var mesh = water ? chunk.WaterMesh : chunk.Mesh;
            if (mesh.IndexCount == 0 || !frustum.Intersects(chunk.Bounds)) continue;

            _device.SetVertexBuffer(mesh.VertexBuffer);
            _device.Indices = mesh.IndexBuffer;
            _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, mesh.IndexCount / 3);
            if (countDrawn) ChunksDrawn++;
        }
    }

    /// <summary>Fog colour and range: the sky's colour far away normally, a murky blue-green when the camera is submerged.</summary>
    private (Vector3 Color, float Start, float End) FogFor(World world, DayNightCycle sky)
    {
        if (CameraUnderwater) return (UnderwaterColor(sky), 1f, 34f);

        float end = (world.RenderDistance - 0.5f) * Chunk.Size;
        return (sky.SkyColor, end * 0.6f, end);
    }

    private static Vector3 UnderwaterColor(DayNightCycle sky) =>
        new Vector3(0.05f, 0.27f, 0.34f) * (0.15f + 0.85f * sky.Daylight);

    /// <summary>(Re)creates the off-screen targets when the window size changes.</summary>
    private void EnsureTargets()
    {
        int width = _device.PresentationParameters.BackBufferWidth;
        int height = _device.PresentationParameters.BackBufferHeight;
        if (_scene != null && _scene.Width == width && _scene.Height == height) return;

        _scene?.Dispose();
        _refraction?.Dispose();

        // Half-float so the alpha channel can hold distances in blocks. PreserveContents so the scene (and its depth)
        // survives being unbound while we copy it for the water pass.
        _scene = new RenderTarget2D(_device, width, height, false, SurfaceFormat.HalfVector4, DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
        _refraction = new RenderTarget2D(_device, width, height, false, SurfaceFormat.HalfVector4, DepthFormat.None);
    }

    /// <summary>Re-reads the block atlas and water normal map from disk. If a file is broken, the current texture stays in use.</summary>
    public void ReloadTextures()
    {
        try
        {
            var atlas = TextureAtlas.Load(_device);
            var normals = AssetFiles.LoadTexture(_device, "Assets/Textures/water_normal.png");
            _atlas.Dispose();
            _waterNormals.Dispose();
            _atlas = atlas;
            _waterNormals = normals;
            Console.WriteLine("Reloaded block atlas and water normal map.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not reload textures: {e.Message}");
        }
    }

    /// <summary>
    /// Orthographic camera looking along the light direction, centred on the player. Its position is snapped
    /// to whole shadow-map texels so shadow edges don't shimmer as the player moves.
    /// </summary>
    private static Matrix BuildLightMatrix(Camera camera, DayNightCycle sky)
    {
        var dir = sky.LightDirection;
        var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        var view = Matrix.CreateLookAt(camera.Position + dir * LightDistance, camera.Position, up);

        float texel = ShadowTexel;
        var centre = Vector3.Transform(camera.Position, view);
        view *= Matrix.CreateTranslation(
            MathF.Round(centre.X / texel) * texel - centre.X,
            MathF.Round(centre.Y / texel) * texel - centre.Y,
            0);

        var projection = Matrix.CreateOrthographic(ShadowRange, ShadowRange, 0f, LightDistance * 2f);
        return view * projection;
    }

    private static void Set(Effect e, string name, float v) => e.Parameters[name]?.SetValue(v);
    private static void Set(Effect e, string name, Vector2 v) => e.Parameters[name]?.SetValue(v);
    private static void Set(Effect e, string name, Vector3 v) => e.Parameters[name]?.SetValue(v);
    private static void Set(Effect e, string name, Matrix v) => e.Parameters[name]?.SetValue(v);
    private static void Set(Effect e, string name, Texture2D v) => e.Parameters[name]?.SetValue(v);

    public void Dispose()
    {
        Ripples.Dispose();
        _shadowMap.Dispose();
        _scene?.Dispose();
        _refraction?.Dispose();
        _atlas.Dispose();
        _waterNormals.Dispose();
        _blit.Dispose();
    }
}
