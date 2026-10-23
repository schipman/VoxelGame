using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Entities;
using VoxelGame.Terrain;

namespace VoxelGame.Rendering;

/// <summary>
/// Draws all loaded chunk meshes. Each frame has two passes using the Voxel.fx shader:
///   1. Shadow: chunks are drawn from the sun's point of view into a depth-only shadow map.
///   2. Main:   chunks are drawn from the camera, looking up the shadow map to find what the sun can't reach.
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    private const int ShadowMapSize = 2048;
    private const float ShadowRange = 144f;       // width of the area (in blocks) covered by the shadow map
    private const float LightDistance = 200f;     // how far back along the sun direction the shadow camera sits

    private readonly GraphicsDevice _device;
    private readonly Effect _effect;
    private Texture2D _atlas;
    private readonly RenderTarget2D _shadowMap;

    public int ChunksDrawn { get; private set; }

    public WorldRenderer(GraphicsDevice device, ContentManager content)
    {
        _device = device;
        _atlas = TextureAtlas.Load(device);
        _effect = content.Load<Effect>("Shaders/Voxel");

        // One 32-bit float channel holding the depth of the nearest surface the sun sees.
        _shadowMap = new RenderTarget2D(device, ShadowMapSize, ShadowMapSize, false, SurfaceFormat.Single, DepthFormat.Depth24);
    }

    /// <summary>
    /// Pass 1. Call before clearing the screen: switching render targets discards the back buffer.
    /// </summary>
    public void RenderShadows(World world, Camera camera, DayNightCycle sky)
    {
        // At dusk and dawn the light is ~0, so skip the shadow pass entirely.
        if (sky.LightColor.LengthSquared() > 1e-4f)
            DrawShadowMap(world, BuildLightMatrix(camera, sky));
    }

    /// <summary>Pass 2: the visible scene, using the shadow map from RenderShadows.</summary>
    public void Draw(World world, Camera camera, DayNightCycle sky) =>
        DrawScene(world, camera, sky, BuildLightMatrix(camera, sky));

    private void DrawShadowMap(World world, Matrix lightViewProjection)
    {
        _effect.Parameters["ShadowMap"]?.SetValue((Texture2D)null);
        _effect.Parameters["LightViewProjection"].SetValue(lightViewProjection);
        _effect.CurrentTechnique = _effect.Techniques["Shadow"];

        _device.SetRenderTarget(_shadowMap);
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.White, 1f, 0);
        _device.DepthStencilState = DepthStencilState.Default;
        _device.BlendState = BlendState.Opaque;
        _device.RasterizerState = RasterizerState.CullCounterClockwise;

        DrawChunks(world, new BoundingFrustum(lightViewProjection), countDrawn: false);
        _device.SetRenderTarget(null);
    }

    private void DrawScene(World world, Camera camera, DayNightCycle sky, Matrix lightViewProjection)
    {
        float fogEnd = (world.RenderDistance - 0.5f) * Chunk.Size;
        float texelSize = ShadowRange / ShadowMapSize;
        float depthRange = LightDistance * 2f;

        _effect.CurrentTechnique = _effect.Techniques["Main"];
        _effect.Parameters["ViewProjection"].SetValue(camera.View * camera.Projection);
        _effect.Parameters["LightViewProjection"].SetValue(lightViewProjection);
        _effect.Parameters["CameraPosition"].SetValue(camera.Position);
        _effect.Parameters["LightDirection"].SetValue(sky.LightDirection);
        _effect.Parameters["LightColor"].SetValue(sky.LightColor);
        _effect.Parameters["AmbientColor"].SetValue(sky.AmbientColor);
        _effect.Parameters["BlockLightColor"].SetValue(new Vector3(1.3f, 1.0f, 0.62f));
        _effect.Parameters["FogColor"].SetValue(sky.SkyColor);
        _effect.Parameters["FogStart"].SetValue(fogEnd * 0.6f);
        _effect.Parameters["FogEnd"].SetValue(fogEnd);
        _effect.Parameters["ShadowMapSize"].SetValue((float)ShadowMapSize);
        _effect.Parameters["ShadowBias"].SetValue(texelSize * 1.5f / depthRange);
        _effect.Parameters["ShadowNormalOffset"].SetValue(texelSize * 1.5f);
        _effect.Parameters["BlockTexture"].SetValue(_atlas);
        _effect.Parameters["ShadowMap"].SetValue(_shadowMap);

        // SpriteBatch changes these, so reset them every frame.
        _device.DepthStencilState = DepthStencilState.Default;
        _device.BlendState = BlendState.Opaque;
        _device.RasterizerState = RasterizerState.CullCounterClockwise;
        _device.SamplerStates[0] = SamplerState.PointClamp;
        _device.SamplerStates[1] = SamplerState.PointClamp;

        ChunksDrawn = 0;
        DrawChunks(world, new BoundingFrustum(camera.View * camera.Projection), countDrawn: true);
    }

    private void DrawChunks(World world, BoundingFrustum frustum, bool countDrawn)
    {
        _effect.CurrentTechnique.Passes[0].Apply();
        foreach (var chunk in world.Chunks)
        {
            if (chunk.IndexCount == 0 || !frustum.Intersects(chunk.Bounds)) continue;

            _device.SetVertexBuffer(chunk.VertexBuffer);
            _device.Indices = chunk.IndexBuffer;
            _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, chunk.IndexCount / 3);
            if (countDrawn) ChunksDrawn++;
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

        float texel = ShadowRange / ShadowMapSize;
        var centre = Vector3.Transform(camera.Position, view);
        view *= Matrix.CreateTranslation(
            MathF.Round(centre.X / texel) * texel - centre.X,
            MathF.Round(centre.Y / texel) * texel - centre.Y,
            0);

        var projection = Matrix.CreateOrthographic(ShadowRange, ShadowRange, 0f, LightDistance * 2f);
        return view * projection;
    }

    /// <summary>Re-reads blocks.png from disk. If the file is broken or half-saved, the current atlas stays in use.</summary>
    public void ReloadAtlas()
    {
        try
        {
            var fresh = TextureAtlas.Load(_device);
            _atlas.Dispose();
            _atlas = fresh;
            Console.WriteLine("Reloaded block texture atlas.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not reload block texture atlas: {e.Message}");
        }
    }

    public void Dispose()
    {
        _shadowMap.Dispose();
        _atlas.Dispose();
    }
}
