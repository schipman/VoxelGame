using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using VoxelGame.Blocks;
using VoxelGame.Diagnostics;
using VoxelGame.Entities;
using VoxelGame.Input;
using VoxelGame.Items;
using VoxelGame.Persistence;
using VoxelGame.Rendering;
using VoxelGame.Settings;
using VoxelGame.Terrain;
using VoxelGame.UI;
using VoxelGame.UI.Screens;

namespace VoxelGame;

/// <summary>Top-level game class: owns the major systems and calls them in order each frame.</summary>
public sealed class VoxelGameApp : Game
{
    private const float MouseSensitivity = 0.0025f;   // radians per pixel at 100%
    private const double AutosaveSeconds = 30;

    private readonly GraphicsDeviceManager _graphics;
    private readonly InputState _input = new();
    private readonly InputState _noInput = new();      // never updated, so always "no keys pressed": fed to the player while a menu is open
    private readonly Camera _camera = new();
    private readonly Inventory _inventory = new();
    private readonly DayNightCycle _sky = new();
    private readonly LaunchOptions _options;
    private GameSettings _settings;
    private BlockInteraction _interaction;
    private CelestialRenderer _celestials;
    private BlockOutlineRenderer _outline;
    private SpriteBatch _spriteBatch;
    private Texture2D _pixel;
    private WorldRenderer _renderer;
    private World _world;
    private WorldStorage _storage;
    private Player _player;

    private UiManager _ui;
    private BlockIconAtlas _icons;
    private PixelFont _font;
    private Hud _hud;

    private double _titleTimer;
    private double _autosaveTimer;
    private bool _wasActive;
    private bool _mouseGate = true;     // swallows the click that closed a menu, so it doesn't also break a block
    private bool _iconsDirty = true;
    private (int Slot, BlockType Block) _lastHeld;
    private Benchmark _bench;
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private double _updateMs, _drawMs;
    private double _lastFrameSeconds = 1 / 60.0;

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

    /// <summary>Start-up phase timings, shown by --bench so slow spots are easy to find.</summary>
    private readonly System.Diagnostics.Stopwatch _startupClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<string> _startupPhases = new();
    private void Phase(string name) { _startupPhases.Add($"{name} {_startupClock.ElapsedMilliseconds} ms"); _startupClock.Restart(); }

    protected override void LoadContent()
    {
        Phase("before LoadContent (process start to here)");
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });

        _settings = _options.Bench ? new GameSettings() : GameSettings.Load(GameSettings.DefaultPath);
        _settings.Clamp();
        if (_options.Distance is { } distance) _settings.RenderDistance = distance;
        if (_options.Size is { } size)
        {
            _graphics.PreferredBackBufferWidth = size.W;
            _graphics.PreferredBackBufferHeight = size.H;
            _graphics.ApplyChanges();
        }
        if (_options.Bench)
        {
            // Fixed conditions, no frame-rate cap, so runs are comparable.
            _bench = new Benchmark();
            _settings.ShowDebugText = false;
            IsFixedTimeStep = false;
            _graphics.SynchronizeWithVerticalRetrace = false;
            _graphics.ApplyChanges();
        }
        if (_options.Survival) _settings.GameMode = GameMode.Survival;

        _renderer = new WorldRenderer(GraphicsDevice, Content);
        _celestials = new CelestialRenderer(_spriteBatch, _pixel);
        _outline = new BlockOutlineRenderer(GraphicsDevice);
        _interaction = new BlockInteraction(_inventory);
        Phase("renderer, shaders, textures");

        // A new random world every launch, unless a seed is given. Worlds are saved by seed, so
        // launching with --seed=N again brings back that world with everything built or dug in it.
        int seed = _options.Bench ? Benchmark.Seed : _options.Seed ?? Random.Shared.Next();
        _storage = new WorldStorage(WorldStorage.DefaultRoot, seed);
        var level = _storage.LoadLevel();
        // Chunks are generated and meshed on background threads; keep a couple of cores free for the game itself.
        int workers = Math.Clamp(Environment.ProcessorCount - 2, 1, 6);
        _world = new World(GraphicsDevice, seed, _settings.RenderDistance, _storage, workers);
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
        LoadInventory(level);
        Phase("world setup, spawn search");

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
        _world.LoadAll(_player.Position);
        if (level == null) MoveToClearSpot();
        Phase($"load {_world.Chunks.Count} chunks");

        BuildUi();
        ApplyFullscreen();   // start fullscreen if that was left on
        ApplyLaunchOptions();
        Phase("UI, icons, launch options");
    }

    // ---- UI ----

    private void BuildUi()
    {
        _font = PixelFont.Load(GraphicsDevice);
        _icons = new BlockIconAtlas(GraphicsDevice);
        _ui = new UiManager(_spriteBatch, new UiRenderer(_spriteBatch, _pixel, _font, _icons));
        _hud = new Hud(_inventory);
        _ui.Hud = _hud;
        _ui.ScaleChanged += () => _iconsDirty = true;
    }

    private PauseActions MakePauseActions() => new()
    {
        SaveGame = () => { SaveWorld(force: true); _ui.ShowMessage("Game saved"); },
        SaveAndQuit = Exit,   // OnExiting saves
    };

    private void OpenPause() => _ui.Open(new PauseScreen(_settings, MakePauseActions()));

    private void ToggleInventory()
    {
        if (_ui.Top is InventoryScreen) _ui.CloseTop();
        else if (!_ui.HasScreen) _ui.Open(new InventoryScreen(_inventory, _settings));
    }

    /// <summary>Starting items: a full hotbar of building blocks in creative, nothing in survival.</summary>
    private void LoadInventory(LevelData level)
    {
        _inventory.Clear();
        if (level?.Inventory != null)
        {
            _inventory.FromSaveData(level.Inventory.Select(s => (s.Slot, s.Block, s.Count)));
            _inventory.SelectedSlot = Math.Clamp(level.SelectedSlot, 0, Inventory.HotbarSize - 1);
        }
        else if (_settings.GameMode == GameMode.Creative)
        {
            var starter = new[] { BlockType.Grass, BlockType.Dirt, BlockType.Stone, BlockType.Sand, BlockType.OakLog, BlockType.OakLeaves, BlockType.Lamp, BlockType.DimLamp, BlockType.Water };
            for (int i = 0; i < starter.Length; i++) _inventory[i] = new ItemStack(starter[i], ItemStack.MaxCount);
        }
    }

    // ---- Frame ----

    protected override void Update(GameTime gameTime)
    {
        double updateStart = _frameClock.Elapsed.TotalMilliseconds;
        UpdateCore(gameTime);
        _updateMs = _frameClock.Elapsed.TotalMilliseconds - updateStart;
    }

    private void UpdateCore(GameTime gameTime)
    {
        float dt = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.05f);

        // While a menu is open the mouse is a normal cursor; otherwise it is grabbed for looking around.
        bool mouseGrabbed = !_ui.HasScreen && IsActive;
        var center = new Point(GraphicsDevice.Viewport.Width / 2, GraphicsDevice.Viewport.Height / 2);
        _input.Update(mouseGrabbed, center);

        // Switching to another window pauses the game, like most games do.
        if (_wasActive && !IsActive && !_ui.HasScreen && _bench == null) OpenPause();
        _wasActive |= IsActive;

        if (_input.WasPressed(Keys.Escape))
        {
            if (!_ui.HandleEscape() && !_ui.HasScreen) OpenPause();
        }
        if (_input.WasPressed(Keys.E)) ToggleInventory();
        if (_input.WasPressed(Keys.F3)) { _settings.ShowDebugText = !_settings.ShowDebugText; _settings.Save(GameSettings.DefaultPath); }
        if (_input.WasPressed(Keys.F5)) ReloadAssets();   // edit blocks.png, tab back, press F5

        if (_input.WasPressed(Keys.F11)) { _settings.Fullscreen = !_settings.Fullscreen; _settings.Save(GameSettings.DefaultPath); }

        // Settings take effect straight away.
        ApplyFullscreen();
        _renderer.ShadowsEnabled = _settings.Shadows;
        _world.SetRenderDistance(_settings.RenderDistance);
        _camera.FarPlane = (_world.RenderDistance + 2) * Chunk.Size;
        _camera.FieldOfView = MathHelper.ToRadians(_settings.FieldOfView);

        _ui.Update(_input, dt, new Point(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height), _settings.UiScale);
        IsMouseVisible = _ui.HasScreen;

        // After a menu closes, ignore the mouse until the button that closed it is let go.
        if (_ui.HasScreen) _mouseGate = true;
        else if (_mouseGate && !_input.LeftHeld && !_input.RightHeld) _mouseGate = false;

        bool playing = !_ui.HasScreen && IsActive;                  // the player controls the character
        bool simulating = !_ui.PausesGame;                          // the world moves on (not while paused)
        var gameInput = playing ? _input : _noInput;

        if (playing)
            _camera.Rotate(-_input.MouseDelta.X * MouseSensitivity * _settings.MouseSensitivity / 100f,
                           -_input.MouseDelta.Y * MouseSensitivity * _settings.MouseSensitivity / 100f);

        if (simulating)
        {
            if (_bench == null) _sky.Update(dt, playing && _input.IsKeyDown(Keys.T) ? 60f : 1f);   // hold T to fast-forward time

            var previousXZ = new Vector2(_player.Position.X, _player.Position.Z);
            _player.Update(dt, gameInput, _camera.Yaw, _world);
            StirWater(previousXZ, dt);
            if (_options.Rain) SprinkleRain(dt);

            _interaction.Update(dt, gameInput, _camera, _world, _player, _settings.GameMode, playing && !_mouseGate);
            _world.Tick(dt);   // water flow
        }
        _world.Update(_player.Position, maxGenerate: 8, maxMesh: 8, timeBudgetMs: 4);

        _camera.Position = _player.EyePosition;

        // Is the camera under water? (Looking up at the surface from below, the water shader takes over.)
        var eye = _camera.Position;
        _renderer.CameraUnderwater = _world.WaterSurface((int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Y), (int)MathF.Floor(eye.Z)) is { } surface && surface > eye.Y;

        UpdateHud();

        _autosaveTimer += gameTime.ElapsedGameTime.TotalSeconds;
        if (_autosaveTimer >= AutosaveSeconds)
        {
            _autosaveTimer = 0;
            SaveWorld();
        }

        UpdateTitle(gameTime);
        base.Update(gameTime);
    }

    private void UpdateHud()
    {
        _hud.DebugVisible = _settings.ShowDebugText;
        if (_settings.ShowDebugText)
        {
            var p = _player.Position;
            string biome = BiomeRegistry.Get(_world.Generator.GetBiomeAt((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z))).Name;
            _hud.DebugText =
                $"{_fps} fps    seed {_world.Seed}    {_sky.Clock}\n" +
                $"xyz {p.X:F1} {p.Y:F1} {p.Z:F1}\n" +
                $"{biome}    chunks {_renderer.ChunksDrawn}/{_world.Chunks.Count}" +
                (_player.IsFlying ? "    flying" : _player.IsSubmerged ? "    underwater" : _player.IsInWater ? "    swimming" : "");
        }

        // Show the name of what is in hand whenever it changes.
        var held = (_inventory.SelectedSlot, _interaction.HeldBlock);
        if (held != _lastHeld)
        {
            _lastHeld = held;
            _hud.ShowItemName(held.Item2 == BlockType.Air ? "" : BlockRegistry.Get(held.Item2).Name);
        }
    }

    private int _fps;
    private double _fpsTimer;
    private int _fpsFrames;

    protected override void Draw(GameTime gameTime)
    {
        double drawStart = _frameClock.Elapsed.TotalMilliseconds;
        _lastFrameSeconds = gameTime.ElapsedGameTime.TotalSeconds;
        DrawCore(gameTime);
        _drawMs = _frameClock.Elapsed.TotalMilliseconds - drawStart;
        if (_bench != null) RunBenchmark();
    }

    private bool _startupReported;

    private void RunBenchmark()
    {
        if (!_startupReported)
        {
            _startupReported = true;
            double ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            _bench.StartupLine = $"start-up: {ms / 1000:F2} s from launch to the first frame (render distance {_world.RenderDistance}, {_world.Chunks.Count} chunks loaded)\n    "
                                 + string.Join("\n    ", _startupPhases);
        }
        if (_bench.CurrentSpeed > 0)
        {
            float yaw = _bench.CurrentYaw;
            _player.Position += new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw)) * _bench.CurrentSpeed * (float)_lastFrameSeconds;
        }
        if (_bench.TakeTeleport() is { } first) BenchTeleport(first);
        if (_bench.EndFrame(_updateMs, _drawMs, _renderer.TrianglesDrawn, _renderer.DrawCalls, _world.CountPendingWork(_player.Position)) is { } next)
            BenchTeleport(next);
        if (_bench.Finished) Exit();
    }

    /// <summary>Moves the flying camera to the nearest spot of a biome, a given height above the ground, looking a given way.</summary>
    private void BenchTeleport((Biome Biome, float Lift, float Yaw, float Pitch) scene)
    {
        var from = _player.Position;
        var at = _world.Generator.FindBiome(scene.Biome, (int)from.X, (int)from.Z) ?? ((int)from.X, (int)from.Z);
        _player.IsFlying = true;
        _player.Position = new Vector3(at.Item1 + 0.5f, _world.Generator.GetHeight(at.Item1, at.Item2) + 2 + scene.Lift, at.Item2 + 0.5f);
        _camera.SetRotation(scene.Yaw, scene.Pitch);
        _sky.Time = 0.4f;
    }

    private void DrawCore(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _camera.AspectRatio = GraphicsDevice.Viewport.AspectRatio;

        _fpsTimer += dt;
        _fpsFrames++;
        if (_fpsTimer >= 0.5) { _fps = (int)(_fpsFrames / _fpsTimer); _fpsTimer = 0; _fpsFrames = 0; }

        // Block icons are drawn into a texture; redo them when the textures or UI scale change (this switches render targets).
        if (_iconsDirty)
        {
            _icons.Rebuild(_renderer.BlockAtlas, 16 * _ui.Scale);
            _iconsDirty = false;
        }

        // The world is drawn off screen and copied to the window at the end; see WorldRenderer.
        _renderer.Prepare(_world, _camera, _sky, _ui.PausesGame ? 0f : dt);   // ripples and shadows: these switch render targets, so they go first
        _renderer.BeginScene(_sky);
        if (!_renderer.CameraUnderwater) _celestials.Draw(_camera, _sky);
        _renderer.DrawTerrain(_world, _camera, _sky);
        _renderer.DrawWater(_world, _camera, _sky, _player.Position);
        if (_interaction.Target is { } target && !_ui.HasScreen) _outline.Draw(target, _camera);
        _renderer.Present();

        _ui.Draw();
        base.Draw(gameTime);
    }

    private bool _isFullscreen;
    private Point _windowedSize = new(1280, 720);

    /// <summary>
    /// Switches between a window and borderless fullscreen at the desktop's resolution (no display-mode change, so
    /// it is instant and alt-tab friendly). Remembers the window size to come back to.
    /// </summary>
    private void ApplyFullscreen()
    {
        if (_settings.Fullscreen == _isFullscreen) return;
        _isFullscreen = _settings.Fullscreen;

        if (_isFullscreen)
        {
            _windowedSize = new Point(_graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
            var screen = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            _graphics.HardwareModeSwitch = false;
            _graphics.PreferredBackBufferWidth = screen.Width;
            _graphics.PreferredBackBufferHeight = screen.Height;
        }
        else
        {
            _graphics.PreferredBackBufferWidth = _windowedSize.X;
            _graphics.PreferredBackBufferHeight = _windowedSize.Y;
        }
        _graphics.IsFullScreen = _isFullscreen;
        _graphics.ApplyChanges();
    }

    /// <summary>Re-reads the editable image files: block textures, water normals and the UI font.</summary>
    private void ReloadAssets()
    {
        _renderer.ReloadTextures();
        try { _font.Reload(GraphicsDevice); }
        catch (Exception e) { Console.WriteLine($"Could not reload font: {e.Message}"); }
        _iconsDirty = true;
    }

    // ---- Water ripples ----

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
            _renderer.Ripples.Disturb(drop, drop, 0.4f, 0.2f);
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
            _renderer.Ripples.Disturb(now, now, 0.9f, Math.Min(0.05f * _player.EntrySpeed, 0.3f));

        // Feet touching water is enough: walking through shallows makes ripples too.
        bool touching = _player.WaterSurfaceY > _player.Position.Y + 0.02f;
        if (!touching || dt <= 0f) return;

        float chestDepth = Math.Max(_player.WaterSurfaceY - (_player.Position.Y + 1.0f), 0f);   // how far the surface is above the chest
        float nearSurface = MathF.Exp(-chestDepth * 0.9f);
        float speed = (now - previousXZ).Length() / dt;

        // The wake is pushed a little ahead of the swimmer, so the bow ripples are in front of the camera, not under it.
        float wake = Math.Min(speed * 0.02f, 0.07f) * nearSurface;
        if (wake > 0.01f)
        {
            var ahead = (now - previousXZ) / Math.Max((now - previousXZ).Length(), 1e-4f) * 1.0f;
            _renderer.Ripples.Disturb(previousXZ + ahead, now + ahead, 0.7f, wake);
        }

        // Treading water: the body bobbing leaves a slow ring every so often.
        _treadTimer += dt;
        if (_player.IsInWater && chestDepth < 0.4f && _treadTimer > 0.7f)
        {
            _treadTimer = 0f;
            _renderer.Ripples.Disturb(now, now, 0.8f, 0.07f);
        }
    }

    // ---- Saving ----

    /// <summary>
    /// Writes changed chunks and the level file. A brand-new world nobody has touched is not saved,
    /// unless <paramref name="force"/> is set (the player asked to save).
    /// </summary>
    private void SaveWorld(bool force = false)
    {
        if (!force && !_storage.Exists && !_world.HasBeenModified) return;

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
            Inventory = _inventory.ToSaveData().Select(s => new InventorySlotData { Slot = s.Slot, Block = s.Block, Count = s.Count }).ToList(),
            SelectedSlot = _inventory.SelectedSlot,
        });
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        SaveWorld();
        _settings.Save(GameSettings.DefaultPath);
        base.OnExiting(sender, args);
    }

    // ---- Start-up helpers ----

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

        switch (_options.Screen)
        {
            case "inventory": ToggleInventory(); break;
            case "pause": OpenPause(); break;
            case "settings": OpenPause(); _ui.Open(new SettingsScreen(_settings)); break;
        }
    }

    /// <summary>The window title mirrors the debug text, which is handy when the window is minimised or for scripts.</summary>
    private void UpdateTitle(GameTime gameTime)
    {
        _titleTimer += gameTime.ElapsedGameTime.TotalSeconds;
        if (_titleTimer < 0.5) return;
        _titleTimer = 0;

        var p = _player.Position;
        Window.Title = $"VoxelGame | seed {_world.Seed} | {_sky.Clock} | {_fps} fps | pos {p.X:F1}, {p.Y:F1}, {p.Z:F1} | " +
                       $"chunks {_renderer.ChunksDrawn}/{_world.Chunks.Count} | " +
                       $"{BiomeRegistry.Get(_world.Generator.GetBiomeAt((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z))).Name} | " +
                       $"holding {(_interaction.HeldBlock == BlockType.Air ? "nothing" : BlockRegistry.Get(_interaction.HeldBlock).Name)}" +
                       (_player.IsFlying ? " | FLYING" : "") + (_player.IsSubmerged ? " | UNDERWATER" : _player.IsInWater ? " | SWIMMING" : "") +
                       (_ui.HasScreen ? " | MENU" : "");
    }

    protected override void UnloadContent()
    {
        _world.Dispose();
        foreach (var chunk in _world.Chunks) chunk.Dispose();
        _renderer.Dispose();
        _outline.Dispose();
        _icons.Dispose();
        _font.Dispose();
        base.UnloadContent();
    }
}
