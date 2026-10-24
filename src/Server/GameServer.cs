using System.Diagnostics;
using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Net;
using VoxelGame.Persistence;
using VoxelGame.Settings;
using VoxelGame.Terrain;

namespace VoxelGame.Server;

/// <summary>How to set up a server.</summary>
public sealed class ServerOptions
{
    public int Seed;
    public string SaveRoot = WorldStorage.DefaultRoot;
    public GameMode GameMode = GameMode.Creative;

    /// <summary>The most chunks around a player the server will keep loaded and send, whatever the player asks for.</summary>
    public int MaxRenderDistance = 16;

    /// <summary>Background threads for generating chunks.</summary>
    public int WorkerThreads = 2;

    /// <summary>Don't read or write any files (tests).</summary>
    public bool InMemory;

    public int MaxPlayers = 16;

    /// <summary>Start at this time of day instead of where the save left off (tests, --time).</summary>
    public float? TimeOfDay;
}

/// <summary>
/// The authority on the world. It owns the real copy (generation, water, saving), keeps every player's client supplied
/// with the chunks around them, checks and applies their edits, and tells everybody what changed.
///
/// It runs on its own thread, ticking 20 times a second, and knows nothing about graphics, so the same class serves a
/// singleplayer game (one local player, no sockets), a game opened to the LAN, and a dedicated server with no window.
/// Players connect through <see cref="IServerTransport"/>s: add as many as you like.
/// </summary>
public sealed class GameServer : IDisposable
{
    public const int TicksPerSecond = 20;
    private const float TickSeconds = 1f / TicksPerSecond;
    private const int ChunksPerTickRemote = 8;
    private const int ChunksPerTickLocal = 32;
    private const float ReachLimit = 8f;                   // how far from a player's eyes an edit may be (the client's own reach is 5)
    private const float DayLengthSeconds = 600f;           // keep in step with DayNightCycle
    private const double AutosaveSeconds = 30;
    private const int MaxChangesPerMessage = 4000;

    private readonly ServerOptions _options;
    private readonly WorldStorage _storage;
    private readonly List<IServerTransport> _transports = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _commands = new();
    private readonly Dictionary<int, PlayerSession> _sessions = new();
    private readonly List<PlayerSession> _closing = new();
    private readonly List<(Point Chunk, BlockChangesMessage.Change Change)> _changes = new();
    private readonly Dictionary<int, Point[]> _offsetsByDistance = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly LevelData _level;
    private readonly PlayerSave _legacyPlayer;
    private Thread _thread;
    private volatile bool _stop;
    private volatile bool _forceSave;
    private volatile bool _saveRequested;
    private int _nextId = 1;
    private float _timeOfDay;
    private double _clock;
    private double _nextTimeBroadcast;
    private double _nextAutosave = AutosaveSeconds;

    public World World { get; }
    public LocalServerTransport Local { get; } = new();
    public int Seed => _options.Seed;
    public GameMode GameMode => _options.GameMode;

    /// <summary>Players currently connected (a snapshot; safe to read from any thread).</summary>
    public int PlayerCount => _playerCount;
    private volatile int _playerCount;

    /// <summary>
    /// Freezes the world (water, time) while true. A singleplayer game pauses with its menu; ignored once the game is open
    /// to other players, who can't be made to wait.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>Asks for the world and players to be written on the next tick, even if nothing has changed.</summary>
    public void RequestSave() { _forceSave = _saveRequested = true; _wake.Set(); }

    /// <summary>Raised on the server thread when something notable happens ("Alex joined"). For logging.</summary>
    public event Action<string> Log;

    public GameServer(ServerOptions options)
    {
        _options = options;
        _storage = options.InMemory ? null : new WorldStorage(options.SaveRoot, options.Seed);
        _level = _storage?.LoadLevel();
        _timeOfDay = options.TimeOfDay ?? _level?.TimeOfDay ?? 0.35f;
        if (_level != null && _level.GeneratorVersion != WorldGenerator.Version)
            Console.WriteLine($"Warning: this world was saved with terrain generator v{_level.GeneratorVersion} (now v{WorldGenerator.Version}); " +
                              "edited chunks may not line up with newly generated terrain.");

        // The first player to join a world saved before multiplayer inherits its single saved player.
        if (_storage != null && !_storage.HasPlayers) _legacyPlayer = PlayerSave.FromLegacy(_level, null);

        World = new World(null, options.Seed, options.MaxRenderDistance, _storage, options.WorkerThreads, WorldRole.Server);
        World.BlockChanged += (x, y, z, type, fluid) =>
            _changes.Add((new Point(x >> 4, z >> 4), new BlockChangesMessage.Change { X = x, Y = y, Z = z, Block = type, FluidLevel = (byte)fluid }));

        _transports.Add(Local);
        Local.Wake = () => _wake.Set();
    }

    // ================================================================ lifecycle

    public void Start()
    {
        _thread = new Thread(Run) { Name = "game-server", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Stops the server, saving the world, and waits for it to finish.</summary>
    public void Stop()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join();
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Starts accepting players over the network on a UDP port. Returns an error message, or null if it worked.</summary>
    public string OpenToNetwork(int port)
    {
        string error = null;
        using var done = new ManualResetEventSlim();
        _commands.Enqueue(() =>
        {
            try
            {
                if (_transports.OfType<LiteNetServerTransport>().Any(t => t.Port == port)) return;
                _transports.Add(new LiteNetServerTransport(port));
                Log?.Invoke($"Listening on UDP port {port}");
            }
            catch (IOException e) { error = e.Message; }
            catch (System.Net.Sockets.SocketException e) { error = e.Message; }
            finally { done.Set(); }
        });
        _wake.Set();
        return done.Wait(5000) ? error : "The server did not respond.";
    }

    /// <summary>True if the server accepts players over the network.</summary>
    public bool IsOpenToNetwork => _transports.OfType<LiteNetServerTransport>().Any();

    // ================================================================ main loop

    private void Run()
    {
        var timer = Stopwatch.StartNew();
        double nextTick = 0;
        double last = 0;

        try
        {
            while (!_stop)
            {
                while (_commands.TryDequeue(out var command)) command();

                Pump();

                double now = timer.Elapsed.TotalSeconds;
                if (now >= nextTick)
                {
                    float dt = (float)Math.Min(now - last, 0.25);
                    last = now;
                    _clock = now;
                    Tick(dt);
                    nextTick = Math.Max(nextTick + TickSeconds, now - TickSeconds);   // keep to 20 a second, but don't try to catch up a long stall
                }

                _wake.WaitOne(1);
            }
        }
        catch (Exception e)
        {
            // A bug in the server must not take the player's window down silently. Report, then shut down cleanly.
            Console.WriteLine($"Server crashed: {e}");
        }

        Shutdown();
    }

    /// <summary>Accepts new connections and handles everything players have sent. Cheap, so it runs every loop, not just every tick.</summary>
    private void Pump()
    {
        foreach (var transport in _transports)
        {
            transport.Poll();
            while (transport.TryAccept(out var connection))
                AddSession(connection);
        }

        foreach (var session in _sessions.Values)
        {
            session.Connection.Poll();
            while (session.Connection.TryReceive(out var message))
            {
                try { Handle(session, message); }
                catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
                {
                    Console.WriteLine($"Bad message from {session.Name}: {e.Message}");
                    Kick(session, "Invalid data");
                    break;
                }
            }
            if (session.Connection.State == LinkState.Closed && !_closing.Contains(session)) _closing.Add(session);
        }

        if (_closing.Count > 0)
        {
            foreach (var session in _closing) RemoveSession(session, session.Connection.CloseReason);
            _closing.Clear();
        }

        BroadcastBlockChanges();   // edits reach everyone straight away, not at the next tick
    }

    private void Tick(float dt)
    {
        // Keep the world loaded around every player, generate what is missing, and let water flow.
        var centers = _sessions.Values.Where(s => s.Playing).Select(s => s.Position).ToList();
        int distance = Math.Min(_options.MaxRenderDistance, Math.Max(3, _sessions.Values.Where(s => s.Playing).Select(s => s.RenderDistance).DefaultIfEmpty(8).Max()));
        World.SetRenderDistance(distance);
        World.Update(centers, maxGenerate: 24, maxMesh: 0, timeBudgetMs: 12);
        bool paused = Paused && !IsOpenToNetwork;
        if (!paused) World.Tick(dt);

        foreach (var session in _sessions.Values)
            if (session.Playing) StreamChunks(session);

        BroadcastBlockChanges();
        BroadcastPlayerStates();

        if (!paused) _timeOfDay = (_timeOfDay + dt / DayLengthSeconds) % 1f;
        if (_clock >= _nextTimeBroadcast)
        {
            _nextTimeBroadcast = _clock + 1;
            Broadcast(new TimeMessage { TimeOfDay = _timeOfDay });
        }

        if (_saveRequested || _clock >= _nextAutosave)
        {
            _saveRequested = false;
            _nextAutosave = _clock + AutosaveSeconds;
            SaveAll();
        }
    }

    private void Shutdown()
    {
        foreach (var session in _sessions.Values.ToList())
        {
            session.Connection.Close("Server closed");
            if (session.Playing) SavePlayer(session);
        }
        _sessions.Clear();
        _playerCount = 0;

        SaveAll();
        foreach (var transport in _transports) transport.Stop();
        World.Dispose();
    }

    // ================================================================ players joining and leaving

    private void AddSession(INetConnection connection)
    {
        var session = new PlayerSession(_nextId++, connection) { JoinedAt = _clock };
        if (connection is LocalConnection local) local.OnMessage = () => _wake.Set();
        _sessions[session.Id] = session;
    }

    private void RemoveSession(PlayerSession session, string reason)
    {
        _sessions.Remove(session.Id);
        session.Connection.Close(reason);
        if (!session.Playing) return;

        SavePlayer(session);
        session.Playing = false;
        _playerCount = _sessions.Values.Count(s => s.Playing);

        foreach (var other in _sessions.Values.Where(s => s.Playing))
        {
            other.Connection.Send(new PlayerLeftMessage { PlayerId = session.Id });
            other.Connection.Send(new NoticeMessage { Text = $"{session.Name} left the game" });
        }
        Log?.Invoke($"{session.Name} left ({reason})");
    }

    private void Kick(PlayerSession session, string reason)
    {
        session.Connection.Send(new DisconnectMessage { Reason = reason });
        session.Connection.Close(reason);
        if (!_closing.Contains(session)) _closing.Add(session);
    }

    /// <summary>Allowed names: 1 to 16 letters, digits, spaces or underscores.</summary>
    public static string CleanName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 16) return null;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == ' ') ? name : null;
    }

    private void HandleHello(PlayerSession session, HelloMessage hello)
    {
        if (session.Playing) return;   // already joined; ignore a second Hello

        if (hello.Protocol != MessageCodec.ProtocolVersion)
        {
            Kick(session, $"Incompatible game version (server protocol {MessageCodec.ProtocolVersion}, yours {hello.Protocol})");
            return;
        }
        var name = CleanName(hello.Name);
        if (name == null) { Kick(session, "Invalid player name (use 1 to 16 letters, digits or spaces)"); return; }
        if (_sessions.Values.Any(s => s.Playing && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Kick(session, $"A player named {name} is already connected");
            return;
        }
        if (_sessions.Values.Count(s => s.Playing) >= _options.MaxPlayers) { Kick(session, "The server is full"); return; }

        session.Name = name;
        session.RenderDistance = Math.Clamp(hello.RenderDistance, 3, _options.MaxRenderDistance);

        // Where do they start? Where they left off, or on dry land near the world's centre.
        var saved = _storage?.LoadPlayer(name);
        if (saved == null && _legacyPlayer != null && !_sessions.Values.Any(s => s.Playing && s != session)) saved = _legacyPlayer;
        bool isNew = saved == null;

        var welcome = new WelcomeMessage
        {
            PlayerId = session.Id,
            Seed = _options.Seed,
            GeneratorVersion = WorldGenerator.Version,
            GameMode = _options.GameMode,
            TimeOfDay = _timeOfDay,
            ServerRenderDistance = _options.MaxRenderDistance,
            IsNewPlayer = isNew,
        };
        if (isNew)
        {
            var (sx, sz) = World.Generator.FindSpawn();
            welcome.Position = new Vector3(sx + 0.5f, World.Generator.GetHeight(sx, sz) + 2, sz + 0.5f);
        }
        else
        {
            welcome.Position = new Vector3(saved.X, saved.Y, saved.Z);
            welcome.Yaw = saved.Yaw;
            welcome.Pitch = saved.Pitch;
            welcome.Flying = saved.Flying;
            welcome.HasInventory = saved.Inventory != null;
            welcome.Inventory = saved.Inventory ?? new List<InventorySlotData>();
            welcome.SelectedSlot = saved.SelectedSlot;
        }

        session.Position = welcome.Position;
        session.Yaw = welcome.Yaw;
        session.Pitch = welcome.Pitch;
        session.Flying = welcome.Flying;
        session.Inventory = saved?.Inventory;
        session.SelectedSlot = saved?.SelectedSlot ?? 0;
        session.Playing = true;
        _playerCount = _sessions.Values.Count(s => s.Playing);

        session.Connection.Send(welcome);

        // Introduce everyone to everyone.
        foreach (var other in _sessions.Values.Where(s => s.Playing && s != session))
        {
            session.Connection.Send(new PlayerJoinedMessage { PlayerId = other.Id, Name = other.Name });
            other.Connection.Send(new PlayerJoinedMessage { PlayerId = session.Id, Name = session.Name });
            other.Connection.Send(new NoticeMessage { Text = $"{session.Name} joined the game" });
        }
        Log?.Invoke($"{session.Name} joined ({session.Connection.Description})");
    }

    // ================================================================ messages from players

    private void Handle(PlayerSession session, Message message)
    {
        if (message is HelloMessage hello) { HandleHello(session, hello); return; }
        if (message is DisconnectMessage d) { session.Connection.Close(d.Reason.Length > 0 ? d.Reason : "left the game"); return; }
        if (!session.Playing) return;   // anything else before a successful Hello is ignored

        switch (message)
        {
            case PlayerUpdateMessage u:
                if (!IsFinite(u.Position)) throw new InvalidDataException("non-finite position");
                session.Position = new Vector3(u.Position.X, Math.Clamp(u.Position.Y, -64, 512), u.Position.Z);
                session.Yaw = u.Yaw;
                session.Pitch = u.Pitch;
                session.Flying = u.Flying;
                break;

            case EditRequestMessage edit:
                HandleEdit(session, edit);
                break;

            case InventoryMessage inventory:
                session.Inventory = inventory.Slots;
                session.SelectedSlot = inventory.SelectedSlot;
                break;

            case SetRenderDistanceMessage rd:
                session.RenderDistance = Math.Clamp(rd.RenderDistance, 3, _options.MaxRenderDistance);
                break;

            case TimeSkipMessage skip:
                _timeOfDay = (_timeOfDay + Math.Clamp(skip.Seconds, 0f, 5f) / DayLengthSeconds) % 1f;
                break;
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Checks that a player may make an edit and, if so, makes it (everyone is told through the world's BlockChanged event).</summary>
    private void HandleEdit(PlayerSession session, EditRequestMessage edit)
    {
        bool allowed = EditAllowed(session, edit);
        if (allowed) World.SetBlock(edit.X, edit.Y, edit.Z, edit.Place ? edit.Block : BlockType.Air);
        else
        {
            // Tell the player what is really there, so a client that guessed wrong puts it right.
            var actual = World.GetBlock(edit.X, edit.Y, edit.Z);
            session.Connection.Send(new BlockChangesMessage
            {
                Changes = { new BlockChangesMessage.Change { X = edit.X, Y = edit.Y, Z = edit.Z, Block = actual, FluidLevel = (byte)World.GetFluidLevel(edit.X, edit.Y, edit.Z) } },
            });
        }
    }

    private bool EditAllowed(PlayerSession session, EditRequestMessage edit)
    {
        if (edit.Y < 0 || edit.Y >= Chunk.Height) return false;
        if (!World.TryGetChunk(edit.X >> 4, edit.Z >> 4, out _)) return false;

        var center = new Vector3(edit.X + 0.5f, edit.Y + 0.5f, edit.Z + 0.5f);
        if (Vector3.Distance(session.EyePosition, center) > ReachLimit) return false;

        var current = World.GetBlock(edit.X, edit.Y, edit.Z);
        if (!edit.Place)
            return current != BlockType.Air && current != BlockType.Bedrock && !BlockRegistry.IsLiquid(current);

        if (edit.Block == BlockType.Air || !Enum.IsDefined(edit.Block)) return false;
        if (BlockRegistry.Get(current).IsSolid) return false;   // only into air or water
        if (BlockRegistry.Get(edit.Block).IsSolid && _sessions.Values.Any(s => s.Playing && s.OverlapsBlock(edit.X, edit.Y, edit.Z))) return false;   // not into a player
        return true;
    }

    // ================================================================ sending things out

    private Point[] OffsetsFor(int renderDistance)
    {
        if (_offsetsByDistance.TryGetValue(renderDistance, out var cached)) return cached;

        // Two extra rings beyond what is drawn: the client needs the chunks around each one to light and mesh it.
        int r = renderDistance + 2;
        var list = new List<Point>();
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
                if (dx * dx + dz * dz <= r * r) list.Add(new Point(dx, dz));
        list.Sort((a, b) => (a.X * a.X + a.Y * a.Y).CompareTo(b.X * b.X + b.Y * b.Y));
        return _offsetsByDistance[renderDistance] = list.ToArray();
    }

    /// <summary>Sends a player the chunks they should have, nearest first, a few per tick; tells them to drop ones they've left behind.</summary>
    private void StreamChunks(PlayerSession session)
    {
        var center = session.ChunkPosition;

        int sent = 0;
        foreach (var o in OffsetsFor(session.RenderDistance))
        {
            var key = new Point(center.X + o.X, center.Y + o.Y);
            if (session.SentChunks.Contains(key) || !World.TryGetChunk(key.X, key.Y, out var chunk)) continue;

            session.Connection.Send(new ChunkMessage { ChunkX = key.X, ChunkZ = key.Y, Data = ChunkCodec.Encode(chunk) });
            session.SentChunks.Add(key);
            if (++sent >= (session.Connection is LocalConnection ? ChunksPerTickLocal : ChunksPerTickRemote)) break;
        }

        // Drop chunks well behind the player (with some slack, so walking back and forth at an edge doesn't resend them).
        int keep = session.RenderDistance + 4;
        List<Point> far = null;
        foreach (var key in session.SentChunks)
            if (Math.Abs(key.X - center.X) > keep || Math.Abs(key.Y - center.Y) > keep)
                (far ??= new List<Point>()).Add(key);
        if (far != null)
            foreach (var key in far)
            {
                session.SentChunks.Remove(key);
                session.Connection.Send(new UnloadChunkMessage { ChunkX = key.X, ChunkZ = key.Y });
            }
    }

    /// <summary>Tells every player about the blocks that changed this tick, but only the ones in chunks they have.</summary>
    private void BroadcastBlockChanges()
    {
        if (_changes.Count == 0) return;

        foreach (var session in _sessions.Values)
        {
            if (!session.Playing) continue;

            var message = new BlockChangesMessage();
            foreach (var (chunk, change) in _changes)
            {
                if (!session.SentChunks.Contains(chunk)) continue;
                message.Changes.Add(change);
                if (message.Changes.Count >= MaxChangesPerMessage)
                {
                    session.Connection.Send(message);
                    message = new BlockChangesMessage();
                }
            }
            if (message.Changes.Count > 0) session.Connection.Send(message);
        }
        _changes.Clear();
    }

    private void BroadcastPlayerStates()
    {
        var playing = _sessions.Values.Where(s => s.Playing).ToList();
        if (playing.Count < 2) return;

        foreach (var session in playing)
        {
            var message = new PlayersStateMessage();
            foreach (var other in playing)
                if (other != session)
                    message.Players.Add(new PlayersStateMessage.State { PlayerId = other.Id, Position = other.Position, Yaw = other.Yaw, Pitch = other.Pitch, Flying = other.Flying });
            session.Connection.Send(message);
        }
    }

    private void Broadcast(Message message)
    {
        foreach (var session in _sessions.Values)
            if (session.Playing) session.Connection.Send(message);
    }

    // ================================================================ saving

    /// <summary>A brand-new world nobody has changed leaves nothing behind, unless a save was asked for.</summary>
    private bool ShouldPersist => _storage != null && (_forceSave || _storage.Exists || World.HasBeenModified);

    private void SavePlayer(PlayerSession session)
    {
        if (ShouldPersist) _storage.SavePlayer(session.ToSave());
    }

    private void SaveAll()
    {
        if (!ShouldPersist) return;

        World.SaveModifiedChunks();
        _storage.SaveLevel(new LevelData { Seed = _options.Seed, GeneratorVersion = WorldGenerator.Version, TimeOfDay = _timeOfDay });
        foreach (var session in _sessions.Values.Where(s => s.Playing)) SavePlayer(session);
    }
}
