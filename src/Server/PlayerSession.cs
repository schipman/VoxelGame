using Microsoft.Xna.Framework;
using VoxelGame.Net;
using VoxelGame.Persistence;

namespace VoxelGame.Server;

/// <summary>Everything the server knows about one connected player.</summary>
public sealed class PlayerSession
{
    public const float Width = 0.6f;
    public const float Height = 1.8f;
    public const float EyeHeight = 1.62f;

    public int Id { get; }
    public INetConnection Connection { get; }

    /// <summary>Empty until the player's Hello has been accepted.</summary>
    public string Name { get; set; } = "";
    public bool Playing { get; set; }

    // Where the player says they are. The client simulates its own movement (the server trusts it), and the server
    // relays it to everyone else.
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public bool Flying { get; set; }

    public int RenderDistance { get; set; } = 8;

    /// <summary>The chunks this player's client currently has. Block changes are only sent for these.</summary>
    public HashSet<Point> SentChunks { get; } = new();

    // The inventory as the client last reported it, kept so it can be saved.
    public List<InventorySlotData> Inventory { get; set; }
    public int SelectedSlot { get; set; }

    public double JoinedAt { get; set; }

    public PlayerSession(int id, INetConnection connection)
    {
        Id = id;
        Connection = connection;
    }

    public Point ChunkPosition => new((int)MathF.Floor(Position.X) >> 4, (int)MathF.Floor(Position.Z) >> 4);
    public Vector3 EyePosition => Position + new Vector3(0, EyeHeight, 0);

    /// <summary>True if the player's body overlaps a block cell.</summary>
    public bool OverlapsBlock(int x, int y, int z)
    {
        const float hw = Width / 2;
        return Position.X + hw > x && Position.X - hw < x + 1
            && Position.Y + Height > y && Position.Y < y + 1
            && Position.Z + hw > z && Position.Z - hw < z + 1;
    }

    public PlayerSave ToSave() => new()
    {
        Name = Name,
        X = Position.X, Y = Position.Y, Z = Position.Z,
        Yaw = Yaw, Pitch = Pitch, Flying = Flying,
        Inventory = Inventory,
        SelectedSlot = SelectedSlot,
    };
}
