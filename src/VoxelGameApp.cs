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

        if (_input.WasPressed(Keys.F5)) _renderer.ReloadAtlas();   // edit blocks.png, tab back, press F5

        _sky.Update(dt, _input.IsKeyDown(Keys.T) ? 60f : 1f);   // hold T to fast-forward time

        _player.Update(dt, _input, _camera.Yaw, _world);
        _camera.Position = _player.EyePosition;

        _interaction.Update(dt, _input, _camera, _world, _player, canInteract && _mouseCaptured);

        _world.Update(_player.Position, maxGenerate: 2, maxMesh: 2);

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
        _camera.AspectRatio = GraphicsDevice.Viewport.AspectRatio;
        _renderer.RenderShadows(_world, _camera, _sky);   // must come first: it switches render targets

        GraphicsDevice.Clear(new Color(_sky.SkyColor));
        _celestials.Draw(_camera, _sky);
        _renderer.Draw(_world, _camera, _sky);
        if (_interaction.Target is { } target) _outline.Draw(target, _camera);

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
                if (_world.GetBlock(x, ground + 1, z) == BlockType.Air && _world.GetBlock(x, ground + 2, z) == BlockType.Air &&
                    _world.GetBlock(x, ground + 3, z) == BlockType.Air)
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
                       (_player.IsFlying ? " | FLYING" : "");
    }

    protected override void UnloadContent()
    {
        foreach (var chunk in _world.Chunks) chunk.Dispose();
        _renderer.Dispose();
        _outline.Dispose();
        base.UnloadContent();
    }
}
