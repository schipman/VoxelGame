using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Persistence;
using VoxelGame.Settings;

namespace VoxelGame.Net;

// ============================================================ client to server

/// <summary>The first thing a client sends: who it is and how far it wants to see.</summary>
public sealed class HelloMessage : Message
{
    public override MessageId Id => MessageId.Hello;
    public int Protocol = MessageCodec.ProtocolVersion;
    public string Name = "Player";
    public int RenderDistance = 8;

    public override void Write(BinaryWriter w) { w.Write(Protocol); w.Write(Name); w.Write(RenderDistance); }
    public override void Read(BinaryReader r) { Protocol = r.ReadInt32(); Name = r.ReadString(); RenderDistance = r.ReadInt32(); }
}

/// <summary>Where the player is and what they are doing. Sent about 20 times a second; losing one doesn't matter.</summary>
public sealed class PlayerUpdateMessage : Message
{
    public override MessageId Id => MessageId.PlayerUpdate;
    public override Delivery Delivery => Delivery.Unreliable;
    public Vector3 Position;
    public float Yaw, Pitch;
    public bool Flying;

    public override void Write(BinaryWriter w) { w.WriteVector3(Position); w.Write(Yaw); w.Write(Pitch); w.Write(Flying); }
    public override void Read(BinaryReader r) { Position = r.ReadVector3(); Yaw = r.ReadSingle(); Pitch = r.ReadSingle(); Flying = r.ReadBoolean(); }
}

/// <summary>A request to break or place a block. The server checks it and, if it is allowed, tells everyone.</summary>
public sealed class EditRequestMessage : Message
{
    public override MessageId Id => MessageId.EditRequest;
    public bool Place;
    public int X, Y, Z;
    public BlockType Block;   // what to place (ignored for breaking)

    public override void Write(BinaryWriter w) { w.Write(Place); w.Write(X); w.Write(Y); w.Write(Z); w.Write((byte)Block); }
    public override void Read(BinaryReader r) { Place = r.ReadBoolean(); X = r.ReadInt32(); Y = r.ReadInt32(); Z = r.ReadInt32(); Block = (BlockType)r.ReadByte(); }
}

/// <summary>The player's inventory, so the server can save it and hand it back next time. Blocks go by name.</summary>
public sealed class InventoryMessage : Message
{
    public override MessageId Id => MessageId.Inventory;
    public List<InventorySlotData> Slots = new();
    public int SelectedSlot;

    public override void Write(BinaryWriter w)
    {
        w.Write(SelectedSlot);
        w.Write(Slots.Count);
        foreach (var s in Slots) { w.Write(s.Slot); w.Write(s.Block); w.Write(s.Count); }
    }

    public override void Read(BinaryReader r)
    {
        SelectedSlot = r.ReadInt32();
        int n = r.ReadCount(256);
        Slots = new List<InventorySlotData>(n);
        for (int i = 0; i < n; i++) Slots.Add(new InventorySlotData { Slot = r.ReadInt32(), Block = r.ReadString(), Count = r.ReadInt32() });
    }
}

/// <summary>The player changed their render distance in the settings.</summary>
public sealed class SetRenderDistanceMessage : Message
{
    public override MessageId Id => MessageId.SetRenderDistance;
    public int RenderDistance;
    public override void Write(BinaryWriter w) => w.Write(RenderDistance);
    public override void Read(BinaryReader r) => RenderDistance = r.ReadInt32();
}

/// <summary>Holding T fast-forwards the day. Asks the server to move the clock on.</summary>
public sealed class TimeSkipMessage : Message
{
    public override MessageId Id => MessageId.TimeSkip;
    public float Seconds;
    public override void Write(BinaryWriter w) => w.Write(Seconds);
    public override void Read(BinaryReader r) => Seconds = r.ReadSingle();
}

// ============================================================ server to client

/// <summary>The server's answer to Hello: everything the client needs to start playing.</summary>
public sealed class WelcomeMessage : Message
{
    public override MessageId Id => MessageId.Welcome;
    public int PlayerId;
    public int Seed;
    public int GeneratorVersion;
    public GameMode GameMode;
    public float TimeOfDay;
    public int ServerRenderDistance;

    /// <summary>True the first time this player joins: the client should find a clear spot to stand once the terrain has loaded.</summary>
    public bool IsNewPlayer;
    public Vector3 Position;
    public float Yaw, Pitch;
    public bool Flying;
    public List<InventorySlotData> Inventory = new();
    public int SelectedSlot;
    public bool HasInventory;

    public override void Write(BinaryWriter w)
    {
        w.Write(PlayerId); w.Write(Seed); w.Write(GeneratorVersion); w.Write((byte)GameMode); w.Write(TimeOfDay); w.Write(ServerRenderDistance);
        w.Write(IsNewPlayer); w.WriteVector3(Position); w.Write(Yaw); w.Write(Pitch); w.Write(Flying);
        w.Write(HasInventory); w.Write(SelectedSlot);
        w.Write(Inventory.Count);
        foreach (var s in Inventory) { w.Write(s.Slot); w.Write(s.Block); w.Write(s.Count); }
    }

    public override void Read(BinaryReader r)
    {
        PlayerId = r.ReadInt32(); Seed = r.ReadInt32(); GeneratorVersion = r.ReadInt32(); GameMode = (GameMode)r.ReadByte(); TimeOfDay = r.ReadSingle(); ServerRenderDistance = r.ReadInt32();
        IsNewPlayer = r.ReadBoolean(); Position = r.ReadVector3(); Yaw = r.ReadSingle(); Pitch = r.ReadSingle(); Flying = r.ReadBoolean();
        HasInventory = r.ReadBoolean(); SelectedSlot = r.ReadInt32();
        int n = r.ReadCount(256);
        Inventory = new List<InventorySlotData>(n);
        for (int i = 0; i < n; i++) Inventory.Add(new InventorySlotData { Slot = r.ReadInt32(), Block = r.ReadString(), Count = r.ReadInt32() });
    }
}

/// <summary>One chunk's blocks, fluid levels and river flow, compressed (see ChunkCodec).</summary>
public sealed class ChunkMessage : Message
{
    public override MessageId Id => MessageId.Chunk;
    public int ChunkX, ChunkZ;
    public byte[] Data = Array.Empty<byte>();

    public override void Write(BinaryWriter w) { w.Write(ChunkX); w.Write(ChunkZ); w.WriteBlob(Data); }
    public override void Read(BinaryReader r) { ChunkX = r.ReadInt32(); ChunkZ = r.ReadInt32(); Data = r.ReadBlob(); }
}

/// <summary>The server is no longer sending updates for this chunk (the player has moved away); the client may forget it.</summary>
public sealed class UnloadChunkMessage : Message
{
    public override MessageId Id => MessageId.UnloadChunk;
    public int ChunkX, ChunkZ;
    public override void Write(BinaryWriter w) { w.Write(ChunkX); w.Write(ChunkZ); }
    public override void Read(BinaryReader r) { ChunkX = r.ReadInt32(); ChunkZ = r.ReadInt32(); }
}

/// <summary>A batch of blocks that changed since the last one, in the order they happened.</summary>
public sealed class BlockChangesMessage : Message
{
    public override MessageId Id => MessageId.BlockChanges;

    public struct Change
    {
        public int X, Y, Z;
        public BlockType Block;
        public byte FluidLevel;
    }

    public List<Change> Changes = new();

    public override void Write(BinaryWriter w)
    {
        w.Write(Changes.Count);
        foreach (var c in Changes) { w.Write(c.X); w.Write(c.Y); w.Write(c.Z); w.Write((byte)c.Block); w.Write(c.FluidLevel); }
    }

    public override void Read(BinaryReader r)
    {
        int n = r.ReadCount();
        Changes = new List<Change>(n);
        for (int i = 0; i < n; i++)
            Changes.Add(new Change { X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(), Block = (BlockType)r.ReadByte(), FluidLevel = r.ReadByte() });
    }
}

public sealed class PlayerJoinedMessage : Message
{
    public override MessageId Id => MessageId.PlayerJoined;
    public int PlayerId;
    public string Name = "";
    public override void Write(BinaryWriter w) { w.Write(PlayerId); w.Write(Name); }
    public override void Read(BinaryReader r) { PlayerId = r.ReadInt32(); Name = r.ReadString(); }
}

public sealed class PlayerLeftMessage : Message
{
    public override MessageId Id => MessageId.PlayerLeft;
    public int PlayerId;
    public override void Write(BinaryWriter w) => w.Write(PlayerId);
    public override void Read(BinaryReader r) => PlayerId = r.ReadInt32();
}

/// <summary>Where all the other players are right now.</summary>
public sealed class PlayersStateMessage : Message
{
    public override MessageId Id => MessageId.PlayersState;
    public override Delivery Delivery => Delivery.Unreliable;

    public struct State
    {
        public int PlayerId;
        public Vector3 Position;
        public float Yaw, Pitch;
        public bool Flying;
    }

    public List<State> Players = new();

    public override void Write(BinaryWriter w)
    {
        w.Write(Players.Count);
        foreach (var p in Players) { w.Write(p.PlayerId); w.WriteVector3(p.Position); w.Write(p.Yaw); w.Write(p.Pitch); w.Write(p.Flying); }
    }

    public override void Read(BinaryReader r)
    {
        int n = r.ReadCount(1024);
        Players = new List<State>(n);
        for (int i = 0; i < n; i++)
            Players.Add(new State { PlayerId = r.ReadInt32(), Position = r.ReadVector3(), Yaw = r.ReadSingle(), Pitch = r.ReadSingle(), Flying = r.ReadBoolean() });
    }
}

/// <summary>The time of day, sent every second so everyone's sky agrees.</summary>
public sealed class TimeMessage : Message
{
    public override MessageId Id => MessageId.Time;
    public float TimeOfDay;
    public override void Write(BinaryWriter w) => w.Write(TimeOfDay);
    public override void Read(BinaryReader r) => TimeOfDay = r.ReadSingle();
}

/// <summary>A line of text for the player to see ("Alex joined the game").</summary>
public sealed class NoticeMessage : Message
{
    public override MessageId Id => MessageId.Notice;
    public string Text = "";
    public override void Write(BinaryWriter w) => w.Write(Text);
    public override void Read(BinaryReader r) => Text = r.ReadString();
}

// ============================================================ either way

/// <summary>Sent just before closing a connection, so the other side can say why.</summary>
public sealed class DisconnectMessage : Message
{
    public override MessageId Id => MessageId.Disconnect;
    public string Reason = "";
    public override void Write(BinaryWriter w) => w.Write(Reason);
    public override void Read(BinaryReader r) => Reason = r.ReadString();
}
