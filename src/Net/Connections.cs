using System.Collections.Concurrent;
using LiteNetLib;

namespace VoxelGame.Net;

public enum LinkState { Connecting, Connected, Closed }

/// <summary>
/// One end of a conversation between client and server. The game talks to this and doesn't know or care whether the
/// other end is in the same process or across the internet.
/// </summary>
public interface INetConnection
{
    LinkState State { get; }

    /// <summary>Why the connection closed, if it did.</summary>
    string CloseReason { get; }

    /// <summary>Where the other end is, for logs and messages.</summary>
    string Description { get; }

    void Send(Message message);

    /// <summary>Takes the next message that has arrived, if any.</summary>
    bool TryReceive(out Message message);

    /// <summary>Lets a network connection process its traffic; call regularly from the thread that owns it. (Does nothing for local ones.)</summary>
    void Poll();

    void Close(string reason = "");
}

/// <summary>Something that hands the server new connections: players in this process, or players across the network.</summary>
public interface IServerTransport
{
    void Poll();
    bool TryAccept(out INetConnection connection);
    void Stop();
}

// ======================================================================= in-process

/// <summary>
/// One end of an in-memory connection (singleplayer: the game and the server it started share a process). Messages
/// are still turned into bytes and back, so singleplayer exercises exactly the code multiplayer does.
/// </summary>
public sealed class LocalConnection : INetConnection
{
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private LocalConnection _other;
    private volatile bool _closed;       // this end closed the connection
    private volatile bool _peerClosed;   // the other end did
    private string _closeReason = "";

    /// <summary>Called whenever a message arrives, so a server thread sleeping between ticks can wake up.</summary>
    public Action OnMessage;

    /// <summary>After the other end closes, this end only counts as closed once it has read everything that end sent first.</summary>
    public LinkState State => _closed || (_peerClosed && _inbox.IsEmpty) ? LinkState.Closed : LinkState.Connected;
    public string CloseReason => _closeReason;
    public string Description { get; private set; }

    public static (LocalConnection Client, LocalConnection Server) CreatePair()
    {
        var client = new LocalConnection { Description = "local server" };
        var server = new LocalConnection { Description = "local player" };
        client._other = server;
        server._other = client;
        return (client, server);
    }

    public void Send(Message message)
    {
        if (_closed) return;
        _other._inbox.Enqueue(MessageCodec.Encode(message));
        _other.OnMessage?.Invoke();
    }

    public bool TryReceive(out Message message)
    {
        while (_inbox.TryDequeue(out var bytes))
        {
            try { message = MessageCodec.Decode(bytes); return true; }
            catch (InvalidDataException e) { Console.WriteLine($"Dropped a bad local message: {e.Message}"); }
        }
        message = null;
        return false;
    }

    public void Poll() { }

    public void Close(string reason = "")
    {
        if (_closed) return;
        _closed = true;
        _closeReason = reason;
        _other._closeReason = reason;
        _other._peerClosed = true;
        _other.OnMessage?.Invoke();
    }
}

/// <summary>Accepts connections made inside this process.</summary>
public sealed class LocalServerTransport : IServerTransport
{
    private readonly ConcurrentQueue<INetConnection> _pending = new();

    /// <summary>Called when a new connection arrives, so a sleeping server thread can wake up and accept it.</summary>
    public Action Wake;

    /// <summary>Connects a new local player to the server and returns their end of the connection.</summary>
    public INetConnection Connect()
    {
        var (client, server) = LocalConnection.CreatePair();
        _pending.Enqueue(server);
        Wake?.Invoke();
        return client;
    }

    public void Poll() { }
    public bool TryAccept(out INetConnection connection) => _pending.TryDequeue(out connection);
    public void Stop() { }
}

// ======================================================================= over the network (LiteNetLib / UDP)

/// <summary>Port the game uses unless told otherwise.</summary>
public static class NetDefaults
{
    public const int Port = 24454;
    public const string ConnectionKey = "VoxelGame/" + "1";
}

internal static class DeliveryMapping
{
    public static DeliveryMethod ToLite(Delivery d) => d == Delivery.Unreliable ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;
}

/// <summary>A server-side view of one remote player's connection.</summary>
internal sealed class LiteServerConnection : INetConnection
{
    private readonly NetPeer _peer;
    private readonly ConcurrentQueue<Message> _inbox = new();
    private volatile bool _closed;
    private string _reason = "";

    public LiteServerConnection(NetPeer peer) { _peer = peer; Description = peer.ToString(); }

    public LinkState State => _closed ? LinkState.Closed : LinkState.Connected;
    public string CloseReason => _reason;
    public string Description { get; }

    public void Send(Message message)
    {
        if (_closed) return;
        _peer.Send(MessageCodec.Encode(message), 0, DeliveryMapping.ToLite(message.Delivery));
    }

    public bool TryReceive(out Message message) => _inbox.TryDequeue(out message);
    public void Poll() { }

    public void Close(string reason = "")
    {
        if (_closed) return;
        Send(new DisconnectMessage { Reason = reason });
        _closed = true;
        _reason = reason;
        _peer.Disconnect();
    }

    internal void Deliver(Message message) => _inbox.Enqueue(message);
    internal void MarkClosed(string reason) { _closed = true; if (_reason.Length == 0) _reason = reason; }
}

/// <summary>Listens on a UDP port and hands the server one connection per player who joins.</summary>
public sealed class LiteNetServerTransport : IServerTransport
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private readonly Dictionary<int, LiteServerConnection> _connections = new();
    private readonly Queue<INetConnection> _accepted = new();

    public int Port { get; }

    public LiteNetServerTransport(int port)
    {
        Port = port;
        _manager = new NetManager(_listener) { DisconnectTimeout = 15000 };

        _listener.ConnectionRequestEvent += request => request.AcceptIfKey(NetDefaults.ConnectionKey);
        _listener.PeerConnectedEvent += peer =>
        {
            var connection = new LiteServerConnection(peer);
            _connections[peer.Id] = connection;
            _accepted.Enqueue(connection);
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (_connections.Remove(peer.Id, out var connection)) connection.MarkClosed(DescribeDisconnect(info));
        };
        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            try
            {
                if (_connections.TryGetValue(peer.Id, out var connection))
                    connection.Deliver(MessageCodec.Decode(reader.GetRemainingBytes()));
            }
            catch (InvalidDataException e)
            {
                Console.WriteLine($"Bad packet from {peer}: {e.Message}");
                peer.Disconnect();
            }
            finally { reader.Recycle(); }
        };

        if (!_manager.Start(port)) throw new IOException($"Could not listen on UDP port {port}. Is another game already using it?");
    }

    private static string DescribeDisconnect(DisconnectInfo info) => info.Reason switch
    {
        DisconnectReason.Timeout => "timed out",
        DisconnectReason.RemoteConnectionClose => "left the game",
        _ => info.Reason.ToString(),
    };

    public void Poll() => _manager.PollEvents();

    public bool TryAccept(out INetConnection connection)
    {
        if (_accepted.Count > 0) { connection = _accepted.Dequeue(); return true; }
        connection = null;
        return false;
    }

    public void Stop()
    {
        foreach (var connection in _connections.Values.ToList()) connection.Close("Server closed");
        _manager.PollEvents();
        _manager.Stop();
    }
}

/// <summary>The client's connection to a remote server. Call <see cref="Poll"/> every frame.</summary>
public sealed class LiteNetClientConnection : INetConnection
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private readonly Queue<Message> _inbox = new();
    private NetPeer _peer;
    private LinkState _state = LinkState.Connecting;
    private string _reason = "";

    public LiteNetClientConnection(string host, int port)
    {
        Description = $"{host}:{port}";
        _manager = new NetManager(_listener) { DisconnectTimeout = 15000 };

        _listener.PeerConnectedEvent += peer => { _peer = peer; _state = LinkState.Connected; };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            _state = LinkState.Closed;
            if (_reason.Length == 0)
                _reason = info.Reason switch
                {
                    DisconnectReason.ConnectionFailed => $"Could not reach {Description}",
                    DisconnectReason.Timeout => "Connection timed out",
                    DisconnectReason.RemoteConnectionClose => "Disconnected by the server",
                    DisconnectReason.ConnectionRejected => "The server refused the connection (is it the same game version?)",
                    _ => info.Reason.ToString(),
                };
        };
        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            try { _inbox.Enqueue(MessageCodec.Decode(reader.GetRemainingBytes())); }
            catch (InvalidDataException e) { Console.WriteLine($"Bad packet from server: {e.Message}"); }
            finally { reader.Recycle(); }
        };

        _manager.Start();
        _manager.Connect(host, port, NetDefaults.ConnectionKey);
    }

    public LinkState State => _state;
    public string CloseReason => _reason;
    public string Description { get; }

    public void Send(Message message)
    {
        if (_state != LinkState.Connected || _peer == null) return;
        _peer.Send(MessageCodec.Encode(message), 0, DeliveryMapping.ToLite(message.Delivery));
    }

    public bool TryReceive(out Message message)
    {
        if (_inbox.Count > 0)
        {
            message = _inbox.Dequeue();
            // A reason the server sent before closing is more useful than a generic "disconnected".
            if (message is DisconnectMessage d && d.Reason.Length > 0) _reason = d.Reason;
            return true;
        }
        message = null;
        return false;
    }

    public void Poll() => _manager.PollEvents();

    public void Close(string reason = "")
    {
        if (_state == LinkState.Closed) return;
        if (_state == LinkState.Connected) Send(new DisconnectMessage { Reason = reason });
        _manager.PollEvents();
        _manager.Stop();
        _state = LinkState.Closed;
        if (_reason.Length == 0) _reason = reason;
    }
}

/// <summary>The addresses other machines can reach this one on.</summary>
public static class NetworkAddresses
{
    /// <summary>This machine's IPv4 addresses on its network interfaces (not loopback), most useful first.</summary>
    public static List<string> Local()
    {
        var found = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        found.Add(address.Address.ToString());
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        if (found.Count == 0) found.Add("127.0.0.1");
        return found;
    }
}
