using System.Diagnostics;
using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Net;
using VoxelGame.Persistence;
using VoxelGame.Settings;

static class NetTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }

    static bool Wait(Func<bool> condition, Action pump, double seconds = 5)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { pump(); if (condition()) return true; Thread.Sleep(2); }
        return false;
    }

    public static int Run()
    {
        // ---- messages survive encoding ----
        var samples = new Message[]
        {
            new HelloMessage { Name = "Alex", RenderDistance = 12 },
            new PlayerUpdateMessage { Position = new Vector3(1.5f, 70, -3), Yaw = 1.2f, Pitch = -0.3f, Flying = true },
            new EditRequestMessage { Place = true, X = -5, Y = 64, Z = 900, Block = BlockType.Lamp },
            new InventoryMessage { SelectedSlot = 3, Slots = { new InventorySlotData { Slot = 0, Block = "Stone", Count = 12 } } },
            new SetRenderDistanceMessage { RenderDistance = 6 },
            new TimeSkipMessage { Seconds = 2.5f },
            new WelcomeMessage { PlayerId = 7, Seed = 42, GameMode = GameMode.Survival, TimeOfDay = 0.3f, IsNewPlayer = true, Position = new Vector3(0, 80, 0), HasInventory = true,
                                 Inventory = { new InventorySlotData { Slot = 4, Block = "Dirt", Count = 64 } } },
            new ChunkMessage { ChunkX = -3, ChunkZ = 8, Data = new byte[] { 1, 2, 3, 4, 5 } },
            new UnloadChunkMessage { ChunkX = 1, ChunkZ = 2 },
            new BlockChangesMessage { Changes = { new BlockChangesMessage.Change { X = 1, Y = 2, Z = 3, Block = BlockType.Water, FluidLevel = 4 } } },
            new PlayerJoinedMessage { PlayerId = 2, Name = "Sam" },
            new PlayerLeftMessage { PlayerId = 2 },
            new PlayersStateMessage { Players = { new PlayersStateMessage.State { PlayerId = 1, Position = new Vector3(5, 6, 7), Yaw = 0.5f } } },
            new TimeMessage { TimeOfDay = 0.75f },
            new NoticeMessage { Text = "Sam joined the game" },
            new DisconnectMessage { Reason = "bye" },
        };
        int bad = 0;
        foreach (var m in samples)
        {
            var back = MessageCodec.Decode(MessageCodec.Encode(m));
            bool same = back.GetType() == m.GetType() && MessageCodec.Encode(back).AsSpan().SequenceEqual(MessageCodec.Encode(m));
            if (!same) { bad++; Console.WriteLine($"  {m.GetType().Name} did not round-trip"); }
        }
        var allTypes = typeof(Message).Assembly.GetTypes().Count(t => !t.IsAbstract && typeof(Message).IsAssignableFrom(t));
        Check(bad == 0 && samples.Length == allTypes, $"all {allTypes} message types survive encoding ({samples.Length} tested, {bad} wrong)");

        bool rejected = false;
        try { MessageCodec.Decode(new byte[] { 255, 1, 2 }); } catch (InvalidDataException) { rejected = true; }
        try { MessageCodec.Decode(new byte[] { (byte)MessageId.Chunk, 0, 0 }); rejected &= false; } catch (InvalidDataException) { }
        Check(rejected, "garbage and truncated packets are rejected, not crashed on");

        // ---- in-process pair ----
        var (c, s) = LocalConnection.CreatePair();
        c.Send(new HelloMessage { Name = "Local" });
        s.TryReceive(out var got);
        Check(got is HelloMessage { Name: "Local" }, "a local connection delivers messages");
        s.Send(new NoticeMessage { Text = "a" }); s.Send(new NoticeMessage { Text = "b" }); s.Close("closing");
        c.TryReceive(out var m1);
        Check(c.State == LinkState.Connected && m1 is NoticeMessage { Text: "a" }, "the other end stays open until it has read everything that was sent");
        c.TryReceive(out var m2);
        Check(m2 is NoticeMessage { Text: "b" } && c.State == LinkState.Closed && c.CloseReason == "closing", "then it sees the close, with the reason");

        // ---- real UDP on localhost ----
        const int Port = 24877;
        var server = new LiteNetServerTransport(Port);
        var client = new LiteNetClientConnection("127.0.0.1", Port);
        INetConnection onServer = null;
        bool connected = Wait(() => client.State == LinkState.Connected && onServer != null, () =>
        {
            server.Poll(); client.Poll();
            if (onServer == null && server.TryAccept(out var accepted)) onServer = accepted;
        });
        Check(connected, "a client connects to a server over UDP");

        client.Send(new HelloMessage { Name = "Remote", RenderDistance = 9 });
        Message received = null;
        Wait(() => { server.Poll(); client.Poll(); return onServer.TryReceive(out received); }, () => { });
        Check(received is HelloMessage { Name: "Remote", RenderDistance: 9 }, "client to server message arrives");

        // a big message (a chunk is ~5 KB; test far bigger to prove fragmentation) and ordering
        var big = new byte[300_000]; new Random(1).NextBytes(big);
        onServer.Send(new ChunkMessage { ChunkX = 1, ChunkZ = 2, Data = big });
        for (int i = 0; i < 50; i++) onServer.Send(new TimeMessage { TimeOfDay = i });
        var order = new List<Message>();
        Wait(() => { server.Poll(); client.Poll(); while (client.TryReceive(out var mm)) order.Add(mm); return order.Count >= 51; }, () => { });
        Check(order.Count == 51 && order[0] is ChunkMessage cm && cm.Data.AsSpan().SequenceEqual(big), "a 300 KB message arrives intact (fragmented)");
        bool inOrder = order.Skip(1).Select((x, i) => x is TimeMessage t && (int)t.TimeOfDay == i).All(b => b);
        Check(inOrder, "reliable messages arrive in the order they were sent");

        // unreliable stream: most should arrive on localhost
        for (int i = 0; i < 100; i++) client.Send(new PlayerUpdateMessage { Position = new Vector3(i, 0, 0) });
        int updates = 0;
        Wait(() => { server.Poll(); client.Poll(); while (onServer.TryReceive(out var mm)) if (mm is PlayerUpdateMessage) updates++; return updates >= 90; }, () => { }, 2);
        Check(updates >= 90, $"unreliable position updates mostly arrive on localhost ({updates} of 100)");

        // disconnect, with reason
        client.Close("test over");
        Wait(() => { server.Poll(); return onServer.State == LinkState.Closed; }, () => { });
        Check(onServer.State == LinkState.Closed, "the server notices when a client leaves");

        // a second server on the same port fails cleanly
        bool clean = false;
        try { new LiteNetServerTransport(Port).Stop(); } catch (IOException) { clean = true; }
        server.Stop();
        Check(clean, "starting a second server on a busy port gives a clear error");

        // connecting to nothing reports a failure instead of hanging
        var lost = new LiteNetClientConnection("127.0.0.1", 24999);
        bool failed = Wait(() => { lost.Poll(); return lost.State == LinkState.Closed; }, () => { }, 12);
        Check(failed && lost.CloseReason.Length > 0, $"connecting to a dead address fails with a reason (\"{lost.CloseReason}\")");

        Console.WriteLine(failures == 0 ? "NET ALL OK" : $"NET FAILED: {failures}");
        return failures;
    }
}
