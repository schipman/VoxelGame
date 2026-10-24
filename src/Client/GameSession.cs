using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;
using VoxelGame.Net;
using VoxelGame.Persistence;
using VoxelGame.Server;
using VoxelGame.Settings;
using VoxelGame.Terrain;

namespace VoxelGame.Client;

public enum SessionState
{
    /// <summary>Waiting for the server to accept us.</summary>
    Connecting,
    /// <summary>Accepted; waiting for the terrain around us to arrive.</summary>
    Loading,
    /// <summary>In the world.</summary>
    Playing,
    /// <summary>Over: we left, the server closed, or the connection failed. See <see cref="GameSession.CloseReason"/>.</summary>
    Closed,
}

/// <summary>
/// The player's connection to a game, from the client's side. It talks to a <see cref="GameServer"/> only through
/// messages, so it makes no difference whether that server runs in this very process (singleplayer, or a game you host)
/// or on another machine: the session keeps its own copy of the world, filled in by what the server sends, and
/// everything the player does goes back as a request.
/// </summary>
public sealed class GameSession : IDisposable
{
    private const float UpdateInterval = 0.05f;   // how often our position is sent

    private readonly GraphicsDevice _device;
    private readonly int _requestedRenderDistance;
    private readonly Dictionary<int, RemotePlayer> _others = new();
    private bool _helloSent;
    private float _updateTimer;
    private string _disconnectReason;

    /// <summary>The server running in this process, if there is one (singleplayer or hosting). Null when joined to someone else's game.</summary>
    public GameServer Server { get; }
    public INetConnection Connection { get; }

    public SessionState State { get; private set; } = SessionState.Connecting;
    public string CloseReason { get; private set; } = "";

    /// <summary>Our copy of the world. Null until the server has welcomed us.</summary>
    public World World { get; private set; }
    public WelcomeMessage Welcome { get; private set; }
    public GameMode GameMode => Welcome?.GameMode ?? GameMode.Creative;
    public int PlayerId => Welcome?.PlayerId ?? 0;
    public string Name { get; }

    /// <summary>How far we can see: what we asked for, or less if the server won't send that much.</summary>
    public int RenderDistance { get; private set; }

    public IEnumerable<RemotePlayer> Others => _others.Values;

    /// <summary>Time of day as the server last said, and whether it has said something new since this was last cleared.</summary>
    public float ServerTime { get; private set; }
    public bool TimeChanged { get; set; }

    /// <summary>Something the server wants shown on screen ("Alex joined the game").</summary>
    public event Action<string> Notice;

    /// <summary>One line saying what we are waiting for, for the loading screen.</summary>
    public string Status => State switch
    {
        SessionState.Connecting => $"Connecting to {Connection.Description}...",
        SessionState.Loading => "Loading terrain...",
        SessionState.Closed => CloseReason,
        _ => "",
    };

    public GameSession(GraphicsDevice device, INetConnection connection, GameServer server, string name, int renderDistance)
    {
        _device = device;
        Connection = connection;
        Server = server;
        Name = name;
        _requestedRenderDistance = renderDistance;
        RenderDistance = renderDistance;
    }

    /// <summary>Starts a world in this process and joins it.</summary>
    public static GameSession StartLocal(GraphicsDevice device, ServerOptions options, string playerName, int renderDistance)
    {
        var server = new GameServer(options);
        server.Start();
        return new GameSession(device, server.Local.Connect(), server, playerName, renderDistance);
    }

    /// <summary>Joins a game on another machine.</summary>
    public static GameSession Join(GraphicsDevice device, string host, int port, string playerName, int renderDistance) =>
        new(device, new LiteNetClientConnection(host, port), null, playerName, renderDistance);

    // ================================================================ per frame

    /// <summary>Handles everything the server has sent. Call once a frame.</summary>
    public void Update(float dt)
    {
        if (State == SessionState.Closed) return;

        Connection.Poll();
        if (!_helloSent && Connection.State == LinkState.Connected)
        {
            _helloSent = true;
            Connection.Send(new HelloMessage { Name = Name, RenderDistance = _requestedRenderDistance });
        }

        while (Connection.TryReceive(out var message))
        {
            try { Handle(message); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                Console.WriteLine($"Bad message from server: {e.Message}");
            }
        }

        if (Connection.State == LinkState.Closed && State != SessionState.Closed)
        {
            State = SessionState.Closed;
            CloseReason = !string.IsNullOrEmpty(_disconnectReason) ? _disconnectReason
                        : !string.IsNullOrEmpty(Connection.CloseReason) ? Connection.CloseReason
                        : "Connection lost";
        }

        foreach (var other in _others.Values) other.Update(dt);
    }

    private void Handle(Message message)
    {
        switch (message)
        {
            case WelcomeMessage welcome:
                Welcome = welcome;
                RenderDistance = Math.Min(_requestedRenderDistance, welcome.ServerRenderDistance);
                ServerTime = welcome.TimeOfDay;
                TimeChanged = true;
                // Chunks are unpacked and meshed on worker threads, but never generated: the server does that.
                int workers = Math.Clamp(Environment.ProcessorCount - 2, 1, 6);
                World = new World(_device, welcome.Seed, RenderDistance, null, workers, WorldRole.Client);
                State = SessionState.Loading;
                break;

            case ChunkMessage chunk: World?.ReceiveChunkData(chunk.ChunkX, chunk.ChunkZ, chunk.Data); break;
            case UnloadChunkMessage unload: World?.ForgetChunk(unload.ChunkX, unload.ChunkZ); break;

            case BlockChangesMessage changes when World != null:
                foreach (var c in changes.Changes) World.ApplyRemoteBlock(c.X, c.Y, c.Z, c.Block, c.FluidLevel);
                break;

            case PlayerJoinedMessage joined: _others[joined.PlayerId] = new RemotePlayer(joined.PlayerId, joined.Name); break;
            case PlayerLeftMessage left: _others.Remove(left.PlayerId); break;
            case PlayersStateMessage states:
                foreach (var s in states.Players)
                    if (_others.TryGetValue(s.PlayerId, out var other)) other.SetTarget(s.Position, s.Yaw, s.Pitch, s.Flying);
                break;

            case TimeMessage time: ServerTime = time.TimeOfDay; TimeChanged = true; break;
            case NoticeMessage notice: Notice?.Invoke(notice.Text); break;
            case DisconnectMessage disconnect: _disconnectReason = disconnect.Reason; break;
        }
    }

    /// <summary>
    /// True once there is enough terrain around <paramref name="center"/> to stand on: the nearby chunks are here and
    /// meshed. The rest keeps arriving while we play, unless <paramref name="everything"/> asks to wait for all of it.
    /// </summary>
    public bool TerrainReady(Vector3 center, bool everything)
    {
        if (World == null) return false;
        int radius = everything ? World.RenderDistance + 2 : 3;
        return World.HasChunksAround(center, radius) && World.WorkInFlight == 0 && World.CountPendingWork(center) == 0;
    }

    public void BeginPlaying()
    {
        if (State == SessionState.Loading) State = SessionState.Playing;
    }

    // ================================================================ requests

    /// <summary>Tells the server where we are, about 20 times a second.</summary>
    public void SendMovement(float dt, Vector3 position, float yaw, float pitch, bool flying)
    {
        _updateTimer += dt;
        if (_updateTimer < UpdateInterval || State != SessionState.Playing) return;
        _updateTimer = 0;
        Connection.Send(new PlayerUpdateMessage { Position = position, Yaw = yaw, Pitch = pitch, Flying = flying });
    }

    public void RequestEdit(bool place, int x, int y, int z, BlockType block) =>
        Connection.Send(new EditRequestMessage { Place = place, X = x, Y = y, Z = z, Block = block });

    public void SendInventory(List<InventorySlotData> slots, int selected) =>
        Connection.Send(new InventoryMessage { Slots = slots, SelectedSlot = selected });

    public void SendRenderDistance(int distance)
    {
        distance = Math.Min(distance, Welcome?.ServerRenderDistance ?? distance);
        if (distance == RenderDistance) return;
        RenderDistance = distance;
        Connection.Send(new SetRenderDistanceMessage { RenderDistance = distance });
    }

    public void SendTimeSkip(float seconds) => Connection.Send(new TimeSkipMessage { Seconds = seconds });

    // ================================================================ leaving

    /// <summary>Leaves the game. If this process runs the server it saves and stops it too (this can take a moment).</summary>
    public void Dispose()
    {
        if (Connection.State != LinkState.Closed) Connection.Close("left the game");
        Server?.Stop();
        if (World != null)
        {
            World.Dispose();
            foreach (var chunk in World.Chunks) chunk.Dispose();
        }
    }
}
