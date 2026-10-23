using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using VoxelGame.Blocks;
using VoxelGame.Entities;
using VoxelGame.Input;
using VoxelGame.Persistence;
using VoxelGame.Rendering;
using VoxelGame.Terrain;

namespace VoxelGame;

/// <summary>Top-level game class: owns the major systems and calls them in order each frame.</summary>
public sealed class VoxelGameApp : Game
{
    private const int RenderDistance = 8;       // in chunks
    private const float MouseSensitivity = 0.0025f;
    private const double AutosaveSeconds = 30;

    private readonly GraphicsDeviceManager _graphics;
    private readonly InputState _input = new();
    private readonly Camera _camera = new();
    private readonly BlockInteraction _interaction = new();
    private readonly DayNightCycle _sky = new();
    private readonly LaunchOptions _options;
    private CelestialRenderer _celestials;
    private BlockOutlineRenderer _outline;
    private SpriteBatch _spriteBatch;
    private Texture2D _pixel;
    private WorldRenderer _renderer;
    private World _world;
    private WorldStorage _storage;
    private Player _player;

    private bool _mouseCaptured = true;
    private double _titleTimer;
    private double _autosaveTimer;

    public VoxelGameApp(LaunchOptions options)
    {
        _options = options;
        Content.RootDirectory = "Content";
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true,
        };
        Window.AllowUserResizing = true;
        IsMouseVisible = false;
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });

        _renderer = new WorldRenderer(GraphicsDevice, Content);
        _celestials = new CelestialRenderer(_spriteBatch, _pixel);
        _outline = new BlockOutlineRenderer(GraphicsDevice);

        // A new random world every launch, unless a seed is given. Worlds are saved by seed, so
        // launching with --seed=N again brings back that world with everything built or dug in it.
        int seed = _options.Seed ?? Random.Shared.Next();
        _storage = new WorldStorage(WorldStorage.DefaultRoot, seed);
        var level = _storage.LoadLevel();
        _world = new World(GraphicsDevice, seed, RenderDistance, _storage);
        _camera.FarPlane = (RenderDistance + 2) * Chunk.Size;
        Console.WriteLine($"World seed {seed}: {(level != null ? "loaded from" : "new, saves to")} {_storage.Directory}");

        if (level != null && level.GeneratorVersion != WorldGenerator.Version)
            Console.WriteLine($"Warning: this world was saved with terrain generator v{level.GeneratorVersion} (now v{WorldGenerator.Version}); " +
                              "edited chunks may not line up with newly generated terrain.");

        if (level != null)
        {
            _player = new Player(new Vector3(level.PlayerX, level.PlayerY, level.PlayerZ)) { IsFlying = level.Flying };
            _camera.Rotate(level.Yaw, level.Pitch);
            _sky.Time = level.TimeOfDay;
        }
        else
        {
            // Spawn on dry land near the origin (the world might start in an ocean).
            var (sx, sz) = _world.Generator.FindSpawn();
            _player = new Player(new Vector3(sx + 0.5f, _world.Generator.GetHeight(sx, sz) + 2, sz + 0.5f));
        }

        if (_options.Teleport is { } tp)
            _player.Position = new Vector3(tp.X + 0.5f, _world.Generator.GetHeight(tp.X, tp.Z) + 2, tp.Z + 0.5f);
        else if (_options.FindBiome is { } wanted)
        {
            var found = _world.Generator.FindBiome(wanted, (int)_player.Position.X, (int)_player.Position.Z);
            if (found is { } at)
                _player.Position = new Vector3(at.X + 0.5f, _world.Generator.GetHeight(at.X, at.Z) + 2, at.Z + 0.5f);
            else
                Console.WriteLine($"No {wanted} found within 8192 blocks of the spawn.");
        }

        // Load everything around the player before the first frame.
        _world.Update(_player.Position, int.MaxValue, int.MaxValue);
        if (level == null) MoveToClearSpot();

        ApplyLaunchOptions();
    }

    protected override void Update(GameTime gameTime)
    {
        float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.05f);

        var center = new Point(GraphicsDevice.Viewport.Width / 2, GraphicsDevice.Viewport.Height / 2);
        _input.Update(_mouseCaptured && IsActive, center);

        // The click that re-grabs the mouse must not also break a block.
        bool canInteract = _mouseCaptured && IsActive;

        // Escape frees the mouse; clicking the window grabs it again; Escape while free quits.
        if (_input.WasPressed(Keys.Escape))
        {
            if (_mouseCaptured) _mouseCaptured = false;
            else Exit();
        }
        else if (!_mouseCaptured && IsActive && _input.LeftClicked)
        {
            _mouseCaptured = true;
        }
        IsMouseVisible = !_mouseCaptured;

        if (_mouseCaptured && IsActive)
            _camera.Rotate(-_input.MouseDelta.X * MouseSensitivity, -_input.MouseDelta.Y * MouseSensitivity);

        if (_input.WasPressed(Keys.F5)) _renderer.ReloadTextures();   // edit blocks.png, tab back, press F5

        _sky.Update(dt, _input.IsKeyDown(Keys.T) ? 60f : 1f);   // hold T to fast-forward time

        var previousXZ = new Vector2(_player.Position.X, _player.Position.Z);
        _player.Update(dt, _input, _camera.Yaw, _world);
        _camera.Position = _player.EyePosition;
        StirWater(previousXZ, dt);
        if (_options.Rain) SprinkleRain(dt);

        // Is the camera under water? (Looking up at the surface from below, the water shader takes over.)
        var eye = _camera.Position;
        _renderer.CameraUnderwater = _world.WaterSurface((int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Y), (int)MathF.Floor(eye.Z)) is { } surface && surface > eye.Y;

        _interaction.Update(dt, _input, _camera, _world, _player, canInteract && _mouseCaptured);

        _world.Update(_player.Position, maxGenerate: 2, maxMesh: 2);
        _world.Tick(dt);   // water flow

        _autosaveTimer += gameTime.ElapsedGameTime.TotalSeconds;
        if (_autosaveTimer >= AutosaveSeconds)
        {
            _autosaveTimer = 0;
            SaveWorld();
        }

        UpdateTitle(gameTime);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _camera.AspectRatio = GraphicsDevice.Viewport.AspectRatio;

        // The world is drawn off screen and copied to the window at the end; see WorldRenderer.
        _renderer.Prepare(_world, _camera, _sky, dt);   // ripples and shadows: these switch render targets, so they go first
        _renderer.BeginScene(_sky);
        if (!_renderer.CameraUnderwater) _celestials.Draw(_camera, _sky);
        _renderer.DrawTerrain(_world, _camera, _sky);
        _renderer.DrawWater(_world, _camera, _sky, _player.Position);
        if (_interaction.Target is { } target) _outline.Draw(target, _camera);
        _renderer.Present();

        DrawCrosshair();
        base.Draw(gameTime);
    }

    private void DrawCrosshair()
    {
        var vp = GraphicsDevice.Viewport;
        int cx = vp.Width / 2, cy = vp.Height / 2;
        _spriteBatch.Begin();
        _spriteBatch.Draw(_pixel, new Rectangle(cx - 8, cy - 1, 16, 2), Color.White * 0.8f);
        _spriteBatch.Draw(_pixel, new Rectangle(cx - 1, cy - 8, 2, 16), Color.White * 0.8f);
        _spriteBatch.End();
    }

    private readonly Random _rain = new(11);
    private float _rainTimer;

    /// <summary>Test aid: random raindrop ripples around the player.</summary>
    private void SprinkleRain(float dt)
    {
        _rainTimer += dt;
        while (_rainTimer > 0.05f)
        {
            _rainTimer -= 0.05f;
            var drop = new Vector2(_player.Position.X + (float)(_rain.NextDouble() * 28 - 14), _player.Position.Z + (float)(_rain.NextDouble() * 28 - 14));
            _renderer.Ripples.Disturb(drop, drop, 0.5f, 0.35f);
        }
    }

    private float _treadTimer;

    /// <summary>
    /// Leaves ripples where the player disturbs water: a splash on diving in, a wake while moving through it (swimming
    /// or just wading), and slow rings while treading water at the surface. Deep swimming barely stirs the surface.
    /// </summary>
    private void StirWater(Vector2 previousXZ, float dt)
    {
        var now = new Vector2(_player.Position.X, _player.Position.Z);

        if (_player.JustEnteredWater)
            _renderer.Ripples.Disturb(now, now, 1.4f, Math.Min(0.08f * _player.EntrySpeed, 0.7f));

        // Feet touching water is enough: walking through shallows makes ripples too.
        bool touching = _player.WaterSurfaceY > _player.Position.Y + 0.02f;
        if (!touching || dt <= 0f) return;

        float chestDepth = Math.Max(_player.WaterSurfaceY - (_player.Position.Y + 1.0f), 0f);   // how far the surface is above the chest
        float nearSurface = MathF.Exp(-chestDepth * 0.9f);
        float speed = (now - previousXZ).Length() / dt;

        // The wake is pushed a little ahead of the swimmer, so the bow ripples are in front of the camera, not under it.
        float wake = Math.Min(speed * 0.045f, 0.16f) * nearSurface;
        if (wake > 0.02f)
        {
            var ahead = (now - previousXZ) / Math.Max((now - previousXZ).Length(), 1e-4f) * 1.4f;
            _renderer.Ripples.Disturb(previousXZ + ahead, now + ahead, 1.0f, wake);
        }

        // Treading water: the body bobbing leaves a slow ring every so often.
        _treadTimer += dt;
        if (_player.IsInWater && chestDepth < 0.4f && _treadTimer > 0.7f)
        {
            _treadTimer = 0f;
            _renderer.Ripples.Disturb(now, now, 1.2f, 0.10f);
        }
    }

    /// <summary>Writes changed chunks and the level file. A brand-new world nobody has touched is not saved.</summary>
    private void SaveWorld()
    {
        if (!_storage.Exists && !_world.HasBeenModified) return;

        _world.SaveModifiedChunks();
        _storage.SaveLevel(new LevelData
        {
            Seed = _world.Seed,
            GeneratorVersion = WorldGenerator.Version,
            TimeOfDay = _sky.Time,
            PlayerX = _player.Position.X,
            PlayerY = _player.Position.Y,
            PlayerZ = _player.Position.Z,
            Yaw = _camera.Yaw,
            Pitch = _camera.Pitch,
            Flying = _player.IsFlying,
        });
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        SaveWorld();
        base.OnExiting(sender, args);
    }

    private bool Solid(int x, int y, int z) => BlockRegistry.Get(_world.GetBlock(x, y, z)).IsSolid;

    /// <summary>A new player might be placed inside a tree; nudge them to the nearest column with open air above the ground.</summary>
    private void MoveToClearSpot()
    {
        int bx = (int)MathF.Floor(_player.Position.X), bz = (int)MathF.Floor(_player.Position.Z);
        for (int r = 0; r <= 12; r++)
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                int x = bx + dx, z = bz + dz, ground = _world.Generator.GetHeight(x, z);
                if (!Solid(x, ground + 1, z) && !Solid(x, ground + 2, z) && !Solid(x, ground + 3, z))
                {
                    _player.Position = new Vector3(x + 0.5f, ground + 1.1f, z + 0.5f);
                    return;
                }
            }
    }

    private void ApplyLaunchOptions()
    {
        if (_options.Time is { } time) _sky.Time = time;
        if (_options.Yaw is { } yaw) _camera.Rotate(MathHelper.ToRadians(yaw), 0);
        if (_options.Pitch is { } pitch) _camera.Rotate(0, MathHelper.ToRadians(pitch));
        if (_options.Lift is { } lift) _player.Position += new Vector3(0, lift, 0);
        if (_options.Fly) _player.IsFlying = true;
        if (_options.Spill)
        {
            int sx = (int)MathF.Floor(_player.Position.X), sz = (int)MathF.Floor(_player.Position.Z) - 6;
            _world.SetBlock(sx, _world.Generator.GetHeight(sx, sz) + 5, sz, BlockType.Water);
        }
        if (_options.Lamp)
        {
            int lx = (int)MathF.Floor(_player.Position.X), lz = (int)MathF.Floor(_player.Position.Z) - 3;
            _world.SetBlock(lx, _world.Generator.GetHeight(lx, lz) + 1, lz, BlockType.Lamp);
        }
    }

    // No font asset yet, so debug info goes in the window title.
    private void UpdateTitle(GameTime gameTime)
    {
        _titleTimer += gameTime.ElapsedGameTime.TotalSeconds;
        if (_titleTimer < 0.5) return;
        _titleTimer = 0;

        var p = _player.Position;
        int fps = (int)(1.0 / Math.Max(gameTime.ElapsedGameTime.TotalSeconds, 0.0001));
        Window.Title = $"VoxelGame | seed {_world.Seed} | {_sky.Clock} | {fps} fps | pos {p.X:F1}, {p.Y:F1}, {p.Z:F1} | " +
                       $"chunks {_renderer.ChunksDrawn}/{_world.Chunks.Count} | " +
                       $"{BiomeRegistry.Get(_world.Generator.GetBiome((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z))).Name} | " +
                       $"holding {BlockRegistry.Get(_interaction.SelectedBlock).Name}" +
                       (_player.IsFlying ? " | FLYING" : "") + (_player.IsSubmerged ? " | UNDERWATER" : _player.IsInWater ? " | SWIMMING" : "");
    }

    protected override void UnloadContent()
    {
        foreach (var chunk in _world.Chunks) chunk.Dispose();
        _renderer.Dispose();
        _outline.Dispose();
        base.UnloadContent();
    }
}
