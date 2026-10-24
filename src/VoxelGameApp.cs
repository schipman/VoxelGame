using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using VoxelGame.Blocks;
using VoxelGame.Client;
using VoxelGame.Diagnostics;
using VoxelGame.Entities;
using VoxelGame.Input;
using VoxelGame.Items;
using VoxelGame.Net;
using VoxelGame.Persistence;
using VoxelGame.Rendering;
using VoxelGame.Server;
using VoxelGame.Settings;
using VoxelGame.Terrain;
using VoxelGame.UI;
using VoxelGame.UI.Screens;

namespace VoxelGame;

/// <summary>
/// Top-level game class: owns the window, the renderer and the UI, and calls them in order each frame.
/// The game itself lives in a <see cref="GameSession"/>: a connection to a server, which is either running inside this
/// process (singleplayer, or a game you host) or on another machine.
/// </summary>
public sealed class VoxelGameApp : Game
{
    private const float MouseSensitivity = 0.0025f;   // radians per pixel at 100%

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
    private PlayerAvatarRenderer _avatars;
    private SpriteBatch _spriteBatch;
    private Texture2D _pixel;
    private WorldRenderer _renderer;
    private Player _player;

    // The current game, if any. Null on the title screen.
    private GameSession _session;
    private World _world => _session?.World;
    private StatusScreen _status;
    private bool _launchOptionsApplied;
    private Vector3? _startPosition;     // where to put the player: the server's spawn point, unless --tp or --find says otherwise
    private string _lastInventory = "";
    private double _inventoryTimer;

    private UiManager _ui;
    private BlockIconAtlas _icons;
    private PixelFont _font;
    private Hud _hud;

    private double _titleTimer;
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
        if (_options.Name != null && GameServer.CleanName(_options.Name) is { } name) _settings.PlayerName = name;
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
        _avatars = new PlayerAvatarRenderer(GraphicsDevice);
        Phase("renderer, shaders, textures");

        BuildUi();
        ApplyFullscreen();   // start fullscreen if that was left on
        Window.TextInput += (_, e) => TextBox.Focused?.Type(e.Character);
        Phase("UI, icons");

        // Start a game straight away, unless we were launched with no options: then the title screen asks what to do.
        if (_options.Join != null) JoinGame(_options.Join, _settings.PlayerName);
        else if (_options.ShowTitle) OpenTitle();
        else StartSingleplayer(_options.Bench ? Benchmark.Seed : _options.Seed ?? Random.Shared.Next());
    }

    // ---- Starting and ending games ----

    /// <summary>Starts a world in this process and plays it. Worlds are saved by seed, so a seed that has been played before comes back with everything built or dug in it.</summary>
    private void StartSingleplayer(int seed)
    {
        EndSession();
        var storage = new WorldStorage(WorldStorage.DefaultRoot, seed);
        Console.WriteLine($"World seed {seed}: {(storage.Exists ? "loaded from" : "new, saves to")} {storage.Directory}");

        _session = GameSession.StartLocal(GraphicsDevice, new ServerOptions
        {
            Seed = seed,
            GameMode = _settings.GameMode,
            TimeOfDay = _options.Time,
            MaxRenderDistance = 20,
            // Chunks are generated and meshed on background threads; keep a couple of cores free for the game itself.
            WorkerThreads = Math.Clamp(Environment.ProcessorCount - 2, 1, 6),
        }, _settings.PlayerName, _settings.RenderDistance);
        BeginSession("Starting world");
    }

    private void JoinGame(string address, string playerName)
    {
        EndSession();

        // "host" or "host:port"
        string host = address;
        int port = NetDefaults.Port;
        int colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out int parsed)) { host = address[..colon]; port = parsed; }

        _settings.PlayerName = GameServer.CleanName(playerName) ?? _settings.PlayerName;
        _settings.LastServer = address;
        _settings.Save(GameSettings.DefaultPath);

        _session = GameSession.Join(GraphicsDevice, host, port, _settings.PlayerName, _settings.RenderDistance);
        BeginSession("Joining game");
    }

    private void BeginSession(string title)
    {
        _player = null;
        _interaction = null;
        _startPosition = null;
        _launchOptionsApplied = false;
        _lastInventory = "";
        _session.Notice += text => _ui.ShowMessage(text, 4f);

        _ui.CloseAll();
        TextBox.ReleaseFocus();
        _status = new StatusScreen(title, () => LeaveSession(null));
        _ui.Open(_status);
    }

    /// <summary>The server has accepted us and the terrain around us is here: put the player in the world.</summary>
    private void BeginPlaying()
    {
        var welcome = _session.Welcome;
        _player = new Player(_startPosition ?? welcome.Position) { IsFlying = welcome.Flying };
        _camera.SetRotation(welcome.Yaw, welcome.Pitch);
        _sky.Time = _session.ServerTime;
        _session.TimeChanged = false;
        LoadInventory(welcome);

        // A new player might be placed inside a tree; nudge them to the nearest open spot.
        if (welcome.IsNewPlayer && _startPosition == welcome.Position) MoveToClearSpot();

        _interaction = new BlockInteraction(_inventory, _session.RequestEdit);
        _session.BeginPlaying();

        _ui.CloseAll();
        _status = null;
        _mouseGate = true;

        if (!_launchOptionsApplied)
        {
            _launchOptionsApplied = true;
            ApplyLaunchOptions();
        }
        Phase("join and load terrain");
    }

    /// <summary>Leaves the game and goes back to the title screen, saving first if the world is ours.</summary>
    private void LeaveSession(string message)
    {
        EndSession();
        _ui.CloseAll();
        OpenTitle(message);
    }

    /// <summary>Closes the current game, if any. When this process runs the server, that saves the world (and takes a moment).</summary>
    private void EndSession()
    {
        if (_session == null) return;

        if (_player != null) SyncInventory(force: true);
        _session.Server?.RequestSave();
        _session.Dispose();
        _session = null;
        _player = null;
        _interaction = null;
        _status = null;
    }

    private void OpenTitle(string message = null)
    {
        _ui.CloseAll();
        TextBox.ReleaseFocus();
        _ui.Open(new TitleScreen(new TitleActions
        {
            NewWorld = () => StartSingleplayer(Random.Shared.Next()),
            LoadWorld = OpenWorldList,
            JoinGame = () => OpenJoinScreen(),
            Settings = () => _ui.Open(new SettingsScreen(_settings)),
            Quit = Exit,
        }, message));
    }

    private void OpenWorldList()
    {
        var worlds = WorldStorage.ListWorlds(WorldStorage.DefaultRoot)
            .Select(w => ($"Seed {w.Seed}   {w.LastPlayed:yyyy-MM-dd HH:mm}", w.Seed))
            .ToList();
        _ui.Open(new WorldListScreen(worlds, StartSingleplayer));
    }

    private void OpenJoinScreen(string error = null)
    {
        var screen = new JoinScreen(_settings.LastServer, _settings.PlayerName, JoinGame);
        _ui.Open(screen);
        if (error != null) screen.ShowError(error);
    }

    /// <summary>The connection ended without us asking (the server stopped, kicked us, or could not be reached).</summary>
    private void OnSessionEnded(string reason)
    {
        bool wasJoining = _session.Server == null;
        string address = _settings.LastServer;
        EndSession();
        _ui.CloseAll();
        if (wasJoining && _player == null)
        {
            OpenTitle();
            OpenJoinScreen(reason);
        }
        else OpenTitle(reason);
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

    private PauseActions MakePauseActions()
    {
        var actions = new PauseActions { SaveAndQuit = () => LeaveSession(null) };
        var server = _session?.Server;
        if (server != null)
        {
            // Only the one who runs the world can save it and let others in.
            actions.SaveGame = () => { SyncInventory(force: true); server.RequestSave(); _ui.ShowMessage("Game saved"); };
            if (!server.IsOpenToNetwork) actions.OpenToLan = OpenToLan;
            else actions.HostInfo = HostInfo();
        }
        return actions;
    }

    private static string HostInfo() => "Others join at " + string.Join(" or ", NetworkAddresses.Local().Select(a => $"{a}:{NetDefaults.Port}"));

    private void OpenToLan()
    {
        var error = _session.Server.OpenToNetwork(NetDefaults.Port);
        _ui.CloseAll();
        if (error != null) _ui.ShowMessage($"Could not open to LAN: {error}", 5f);
        else
        {
            Console.WriteLine(HostInfo());
            _ui.ShowMessage(HostInfo(), 8f);
        }
    }

    private void OpenPause() => _ui.Open(new PauseScreen(_settings, MakePauseActions()));

    private void ToggleInventory()
    {
        if (_ui.Top is InventoryScreen) _ui.CloseTop();
        else if (!_ui.HasScreen) _ui.Open(new InventoryScreen(_inventory, _settings));
    }

    /// <summary>The inventory the server says we had, or the starting items: a full hotbar of building blocks in creative, nothing in survival.</summary>
    private void LoadInventory(WelcomeMessage welcome)
    {
        _inventory.Clear();
        if (welcome.HasInventory)
        {
            _inventory.FromSaveData(welcome.Inventory.Select(s => (s.Slot, s.Block, s.Count)));
            _inventory.SelectedSlot = Math.Clamp(welcome.SelectedSlot, 0, Inventory.HotbarSize - 1);
        }
        else if (welcome.GameMode == GameMode.Creative)
        {
            var starter = new[] { BlockType.Grass, BlockType.Dirt, BlockType.Stone, BlockType.Sand, BlockType.OakLog, BlockType.OakLeaves, BlockType.Lamp, BlockType.DimLamp, BlockType.Water };
            for (int i = 0; i < starter.Length; i++) _inventory[i] = new ItemStack(starter[i], ItemStack.MaxCount);
        }
    }

    /// <summary>The server keeps the inventory for saving; tell it when ours changes.</summary>
    private void SyncInventory(bool force = false)
    {
        var slots = _inventory.ToSaveData().Select(s => new InventorySlotData { Slot = s.Slot, Block = s.Block, Count = s.Count }).ToList();
        string signature = _inventory.SelectedSlot + ";" + string.Join(";", slots.Select(s => $"{s.Slot}:{s.Block}:{s.Count}"));
        if (!force && signature == _lastInventory) return;
        _lastInventory = signature;
        _session.SendInventory(slots, _inventory.SelectedSlot);
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
        bool inGame = _player != null;

        // While a menu is open the mouse is a normal cursor; otherwise it is grabbed for looking around.
        bool mouseGrabbed = inGame && !_ui.HasScreen && IsActive;
        var center = new Point(GraphicsDevice.Viewport.Width / 2, GraphicsDevice.Viewport.Height / 2);
        _input.Update(mouseGrabbed, center);

        // Switching to another window pauses the game, like most games do.
        if (_wasActive && !IsActive && inGame && !_ui.HasScreen && _bench == null) OpenPause();
        _wasActive |= IsActive;

        if (_input.WasPressed(Keys.Escape))
        {
            if (!_ui.HandleEscape() && !_ui.HasScreen && inGame) OpenPause();
        }
        if (inGame && _input.WasPressed(Keys.E)) ToggleInventory();
        if (_input.WasPressed(Keys.F3)) { _settings.ShowDebugText = !_settings.ShowDebugText; _settings.Save(GameSettings.DefaultPath); }
        if (_input.WasPressed(Keys.F5)) ReloadAssets();   // edit blocks.png, tab back, press F5

        if (_input.WasPressed(Keys.F11)) { _settings.Fullscreen = !_settings.Fullscreen; _settings.Save(GameSettings.DefaultPath); }

        // Settings take effect straight away.
        ApplyFullscreen();
        _renderer.ShadowsEnabled = _settings.Shadows;
        _camera.FieldOfView = MathHelper.ToRadians(_settings.FieldOfView);
        if (_session?.World != null)
        {
            _session.SendRenderDistance(_settings.RenderDistance);
            _world.SetRenderDistance(_session.RenderDistance);
            _camera.FarPlane = (_world.RenderDistance + 2) * Chunk.Size;
        }

        UpdateSession(dt);
        inGame = _player != null;

        _ui.Update(_input, dt, new Point(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height), _settings.UiScale);
        IsMouseVisible = _ui.HasScreen || !inGame;
        _hud.Root.Visible = inGame;

        // After a menu closes, ignore the mouse until the button that closed it is let go.
        if (_ui.HasScreen) _mouseGate = true;
        else if (_mouseGate && !_input.LeftHeld && !_input.RightHeld) _mouseGate = false;

        if (inGame) UpdatePlaying(dt);

        UpdateTitle(gameTime);
        base.Update(gameTime);
    }

    /// <summary>Handles the connection: what the server has sent, and moving from "connecting" to "loading" to "playing".</summary>
    private void UpdateSession(float dt)
    {
        if (_session == null) return;

        _session.Update(dt);
        if (_session.State == SessionState.Closed)
        {
            OnSessionEnded(_session.CloseReason);
            return;
        }

        if (_status != null) _status.Text = _session.Status;

        if (_session.State == SessionState.Loading)
        {
            // Mesh what has arrived; start once there is solid ground under the player.
            var welcome = _session.Welcome;
            if (_startPosition == null)
            {
                _startPosition = ChooseStart(welcome.Position);
                // Tell the server, so it sends the terrain around where we really are.
                if (_startPosition != welcome.Position)
                    _session.Connection.Send(new PlayerUpdateMessage { Position = _startPosition.Value, Yaw = welcome.Yaw, Pitch = welcome.Pitch, Flying = welcome.Flying });
            }
            var spawn = _startPosition.Value;
            _world.SetRenderDistance(_session.RenderDistance);
            _world.Update(spawn, maxGenerate: 8, maxMesh: 8, timeBudgetMs: 10);
            if (_session.TerrainReady(spawn, everything: _options.Bench)) BeginPlaying();
        }
    }

    /// <summary>Everything that happens in a frame once the player is in the world.</summary>
    private void UpdatePlaying(float dt)
    {
        bool playing = !_ui.HasScreen && IsActive;                  // the player controls the character
        bool simulating = !_ui.PausesGame;                          // the world moves on (not while paused)
        var gameInput = playing ? _input : _noInput;

        // A singleplayer game stands still behind the pause menu.
        if (_session.Server != null) _session.Server.Paused = !simulating;

        if (playing)
            _camera.Rotate(-_input.MouseDelta.X * MouseSensitivity * _settings.MouseSensitivity / 100f,
                           -_input.MouseDelta.Y * MouseSensitivity * _settings.MouseSensitivity / 100f);

        if (_session.TimeChanged)
        {
            _sky.Time = _session.ServerTime;
            _session.TimeChanged = false;
        }

        // Don't let the player fall through ground that hasn't arrived yet.
        bool groundLoaded = _world.TryGetChunk((int)MathF.Floor(_player.Position.X) >> 4, (int)MathF.Floor(_player.Position.Z) >> 4, out _);

        if (simulating)
        {
            if (_bench == null)
            {
                bool fast = playing && _input.IsKeyDown(Keys.T);   // hold T to fast-forward time
                _sky.Update(dt, fast ? 60f : 1f);
                if (fast) _session.SendTimeSkip(dt * 59f);
            }

            if (groundLoaded)
            {
                var previousXZ = new Vector2(_player.Position.X, _player.Position.Z);
                _player.Update(dt, gameInput, _camera.Yaw, _world);
                StirWater(previousXZ, dt);
            }
            if (_options.Rain) SprinkleRain(dt);

            _interaction.Update(dt, gameInput, _camera, _world, _player, _session.GameMode, playing && !_mouseGate);
        }
        _world.Update(_player.Position, maxGenerate: 8, maxMesh: 8, timeBudgetMs: 4);

        _session.SendMovement(dt, _player.Position, _camera.Yaw, _camera.Pitch, _player.IsFlying);
        _inventoryTimer += dt;
        if (_inventoryTimer >= 1) { _inventoryTimer = 0; SyncInventory(); }

        _camera.Position = _player.EyePosition;

        // Is the camera under water? (Looking up at the surface from below, the water shader takes over.)
        var eye = _camera.Position;
        _renderer.CameraUnderwater = _world.WaterSurface((int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Y), (int)MathF.Floor(eye.Z)) is { } surface && surface > eye.Y;

        UpdateHud();
    }

    private void UpdateHud()
    {
        _hud.DebugVisible = _settings.ShowDebugText;
        if (_settings.ShowDebugText)
        {
            var p = _player.Position;
            string biome = BiomeRegistry.Get(_world.Generator.GetBiomeAt((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z))).Name;
            int others = _session.Others.Count();
            _hud.DebugText =
                $"{_fps} fps    seed {_world.Seed}    {_sky.Clock}    {_session.Name}" + (others > 0 ? $"    +{others} player{(others == 1 ? "" : "s")}" : "") + "\n" +
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
        if (_bench != null && _player != null) RunBenchmark();
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

        if (_player == null)
        {
            // Title, join and loading screens: no world to show.
            GraphicsDevice.Clear(new Color(24, 28, 40));
            _ui.Draw();
            base.Draw(gameTime);
            return;
        }

        // The world is drawn off screen and copied to the window at the end; see WorldRenderer.
        _renderer.Prepare(_world, _camera, _sky, _ui.PausesGame ? 0f : dt);   // ripples and shadows: these switch render targets, so they go first
        _renderer.BeginScene(_sky);
        if (!_renderer.CameraUnderwater) _celestials.Draw(_camera, _sky);
        _renderer.DrawTerrain(_world, _camera, _sky);
        _avatars.Draw(_session.Others, _camera);
        _renderer.DrawWater(_world, _camera, _sky, _player.Position);
        if (_interaction.Target is { } target && !_ui.HasScreen) _outline.Draw(target, _camera);
        _renderer.Present();

        DrawNameTags();
        _ui.Draw();
        base.Draw(gameTime);
    }

    /// <summary>Writes each other player's name above their head.</summary>
    private void DrawNameTags()
    {
        var others = _session.Others.Where(p => p.Visible).ToList();
        if (others.Count == 0) return;

        var viewProjection = _camera.View * _camera.Projection;
        var r = _ui.Renderer;
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone,
            transformMatrix: Matrix.CreateScale(_ui.Scale, _ui.Scale, 1f));
        foreach (var p in others)
        {
            var clip = Vector4.Transform(new Vector4(PlayerAvatarRenderer.TagPosition(p), 1), viewProjection);
            if (clip.W <= 0.1f || clip.W > 80) continue;   // behind us, or too far to read

            float sx = (clip.X / clip.W * 0.5f + 0.5f) * GraphicsDevice.Viewport.Width / _ui.Scale;
            float sy = (0.5f - clip.Y / clip.W * 0.5f) * GraphicsDevice.Viewport.Height / _ui.Scale;
            int width = r.TextWidth(p.Name);
            var area = new Rectangle((int)sx - width / 2 - 3, (int)sy - 11, width + 6, 11);
            r.Fill(area, UiTheme.Alpha(0, 0, 0, 120));
            r.Text(p.Name, new Point(area.X + 3, area.Y + 2), Color.White);
        }
        _spriteBatch.End();
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

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        EndSession();   // saves the world if it is ours
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

    /// <summary>Where to start: the spawn point the server gave, or the place --tp or --find asks for.</summary>
    private Vector3 ChooseStart(Vector3 spawn)
    {
        if (_options.Teleport is { } tp)
            return new Vector3(tp.X + 0.5f, _world.Generator.GetHeight(tp.X, tp.Z) + 2, tp.Z + 0.5f);
        if (_options.FindBiome is { } wanted)
        {
            var found = _world.Generator.FindBiome(wanted, (int)spawn.X, (int)spawn.Z);
            if (found is { } at)
                return new Vector3(at.X + 0.5f, _world.Generator.GetHeight(at.X, at.Z) + 2, at.Z + 0.5f);
            Console.WriteLine($"No {wanted} found within 8192 blocks of the spawn.");
        }
        return spawn;
    }

    private void ApplyLaunchOptions()
    {
        if (_options.Yaw is { } yaw) _camera.Rotate(MathHelper.ToRadians(yaw), 0);
        if (_options.Pitch is { } pitch) _camera.Rotate(0, MathHelper.ToRadians(pitch));
        if (_options.Lift is { } lift) _player.Position += new Vector3(0, lift, 0);
        if (_options.Fly) _player.IsFlying = true;
        if (_options.Spill)
        {
            int sx = (int)MathF.Floor(_player.Position.X), sz = (int)MathF.Floor(_player.Position.Z) - 6;
            _session.RequestEdit(true, sx, _world.Generator.GetHeight(sx, sz) + 5, sz, BlockType.Water);
        }
        if (_options.Lamp)
        {
            int lx = (int)MathF.Floor(_player.Position.X), lz = (int)MathF.Floor(_player.Position.Z) - 3;
            _session.RequestEdit(true, lx, _world.Generator.GetHeight(lx, lz) + 1, lz, BlockType.Lamp);
        }
        if (_options.HostPort is { } port && _session.Server != null)
        {
            if (_session.Server.OpenToNetwork(port) is { } error) Console.WriteLine($"Could not open to LAN: {error}");
            else Console.WriteLine(HostInfo());
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

        if (_player == null)
        {
            Window.Title = "VoxelGame" + (_session != null ? " | " + _session.Status : "");
            return;
        }

        var p = _player.Position;
        Window.Title = $"VoxelGame | seed {_world.Seed} | {_sky.Clock} | {_fps} fps | pos {p.X:F1}, {p.Y:F1}, {p.Z:F1} | " +
                       $"chunks {_renderer.ChunksDrawn}/{_world.Chunks.Count} | " +
                       $"{BiomeRegistry.Get(_world.Generator.GetBiomeAt((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z))).Name} | " +
                       $"holding {(_interaction.HeldBlock == BlockType.Air ? "nothing" : BlockRegistry.Get(_interaction.HeldBlock).Name)}" +
                       (_player.IsFlying ? " | FLYING" : "") + (_player.IsSubmerged ? " | UNDERWATER" : _player.IsInWater ? " | SWIMMING" : "") +
                       (_session.Others.Any() ? $" | {_session.Others.Count() + 1} PLAYERS" : "") +
                       (_ui.HasScreen ? " | MENU" : "");
    }

    protected override void UnloadContent()
    {
        EndSession();
        _renderer.Dispose();
        _outline.Dispose();
        _avatars.Dispose();
        _icons.Dispose();
        _font.Dispose();
        base.UnloadContent();
    }
}
