using System.Text;
using Microsoft.Xna.Framework;

namespace VoxelGame.Net;

/// <summary>Identifies each kind of message on the wire (one byte at the front of every packet).</summary>
public enum MessageId : byte
{
    // Client to server
    Hello = 1,
    PlayerUpdate,
    EditRequest,
    Inventory,
    SetRenderDistance,
    TimeSkip,

    // Server to client
    Welcome = 32,
    Chunk,
    UnloadChunk,
    BlockChanges,
    PlayerJoined,
    PlayerLeft,
    PlayersState,
    Time,
    Notice,

    // Either way
    Disconnect = 64,
}

/// <summary>How a message should be delivered.</summary>
public enum Delivery
{
    /// <summary>Arrives, once, in order. For anything that must not be lost (chunks, block changes, inventory).</summary>
    Reliable,

    /// <summary>May be dropped or arrive out of order; a newer one replaces it anyway (player positions).</summary>
    Unreliable,
}

/// <summary>
/// Something sent between client and server. Subclass it, give it an <see cref="Id"/>, and write and read its fields in
/// the same order. Messages are found automatically (see <see cref="MessageCodec"/>), so nothing needs registering.
/// </summary>
public abstract class Message
{
    public abstract MessageId Id { get; }
    public virtual Delivery Delivery => Delivery.Reliable;

    public abstract void Write(BinaryWriter w);
    public abstract void Read(BinaryReader r);
}

/// <summary>Turns messages into bytes and back. The same code serves the in-process and the network transport.</summary>
public static class MessageCodec
{
    /// <summary>Bump when any message changes, so mismatched games refuse to talk to each other.</summary>
    public const int ProtocolVersion = 1;

    private static readonly Dictionary<MessageId, Func<Message>> Factories = BuildFactories();

    private static Dictionary<MessageId, Func<Message>> BuildFactories()
    {
        var factories = new Dictionary<MessageId, Func<Message>>();
        foreach (var type in typeof(Message).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(Message).IsAssignableFrom(type)) continue;
            var make = () => (Message)Activator.CreateInstance(type)!;
            factories[make().Id] = make;
        }
        return factories;
    }

    public static byte[] Encode(Message message)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)message.Id);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            message.Write(writer);
        return stream.ToArray();
    }

    /// <summary>Throws InvalidDataException if the bytes aren't a valid message.</summary>
    public static Message Decode(byte[] data)
    {
        try
        {
            if (data.Length == 0 || !Factories.TryGetValue((MessageId)data[0], out var make))
                throw new InvalidDataException($"unknown message id {(data.Length > 0 ? data[0] : -1)}");

            var message = make();
            using var stream = new MemoryStream(data, 1, data.Length - 1, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            message.Read(reader);
            return message;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("malformed message: " + e.Message, e);
        }
    }

    // ---- Helpers shared by messages ----

    public static void WriteVector3(this BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    public static Vector3 ReadVector3(this BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    /// <summary>Reads a length-prefixed byte array, refusing absurd sizes so a bad packet can't make us allocate gigabytes.</summary>
    public static byte[] ReadBlob(this BinaryReader r, int maxLength = 1 << 20)
    {
        int length = r.ReadInt32();
        if (length < 0 || length > maxLength) throw new InvalidDataException($"blob of {length} bytes");
        return r.ReadBytes(length);
    }

    public static void WriteBlob(this BinaryWriter w, byte[] data) { w.Write(data.Length); w.Write(data); }

    /// <summary>Reads a list count, refusing absurd sizes.</summary>
    public static int ReadCount(this BinaryReader r, int max = 100_000)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > max) throw new InvalidDataException($"list of {count} items");
        return count;
    }
}
