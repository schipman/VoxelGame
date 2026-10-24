using System.Diagnostics;
using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Net;
using VoxelGame.Server;
using VoxelGame.Settings;

static class ServerTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }

    sealed class Fake
    {
        public INetConnection C; public List<Message> Got = new();
        public void Pump() { C.Poll(); while (C.TryReceive(out var m)) Got.Add(m); }
        public T Last<T>() where T : Message => Got.OfType<T>().LastOrDefault();
        public int Count<T>() where T : Message => Got.OfType<T>().Count();
    }

    static bool Wait(Func<bool> cond, Action pump, double s = 10)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < s) { pump(); if (cond()) return true; Thread.Sleep(5); }
        return false;
    }

    public static int Run()
    {
        var server = new GameServer(new ServerOptions { Seed = 1234, InMemory = true, MaxRenderDistance = 4, GameMode = GameMode.Survival });
        server.Start();
        Fake Join(string name)
        {
            var f = new Fake { C = server.Local.Connect() };
            f.C.Send(new HelloMessage { Name = name, RenderDistance = 3 });
            return f;
        }
        var a = Join("Alex"); var b = Join("Bea");
        Action pump = () => { a.Pump(); b.Pump(); };
        Check(Wait(() => a.Last<WelcomeMessage>() != null && b.Last<WelcomeMessage>() != null, pump), "both welcomed");
        var wa = a.Last<WelcomeMessage>();
        Check(wa.Seed == 1234 && wa.IsNewPlayer && wa.GameMode == GameMode.Survival, "welcome contents");
        Check(a.Last<PlayerJoinedMessage>()?.Name == "Bea" || Wait(() => a.Last<PlayerJoinedMessage>() != null, pump), "Alex told about Bea");
        Check(Wait(() => a.Count<ChunkMessage>() >= 20, pump), "chunks streamed");

        // edit: break a block near Alex
        int bx = (int)MathF.Floor(wa.Position.X), bz = (int)MathF.Floor(wa.Position.Z), by = (int)wa.Position.Y - 2;
        // wait until chunk is present at that spot
        a.C.Send(new EditRequestMessage { Place = false, X = bx, Y = by, Z = bz });
        Check(Wait(() => a.Count<BlockChangesMessage>() > 0 && b.Count<BlockChangesMessage>() > 0, pump), "edit broadcast to both");
        var ch = b.Last<BlockChangesMessage>().Changes[0];
        Check(ch.Block == BlockType.Air && ch.X == bx && ch.Y == by, "change is air at position");

        // far edit rejected: server answers with actual block
        int before = a.Count<BlockChangesMessage>();
        a.C.Send(new EditRequestMessage { Place = false, X = bx + 60, Y = 40, Z = bz });
        Check(Wait(() => a.Count<BlockChangesMessage>() > before, pump), "far edit corrected");

        // position relay
        a.C.Send(new PlayerUpdateMessage { Position = new Vector3(wa.Position.X + 2, wa.Position.Y, wa.Position.Z), Yaw = 1 });
        Check(Wait(() => b.Last<PlayersStateMessage>()?.Players.Any(p => Math.Abs(p.Position.X - (wa.Position.X + 2)) < 0.01f) == true, pump), "position relayed");

        // name clash + bad protocol
        var c = new Fake { C = server.Local.Connect() };
        c.C.Send(new HelloMessage { Name = "alex" });
        Check(Wait(() => c.Last<DisconnectMessage>() != null, () => c.Pump()), "duplicate name rejected");
        var d = new Fake { C = server.Local.Connect() };
        d.C.Send(new HelloMessage { Name = "Old", Protocol = 999 });
        Check(Wait(() => d.Last<DisconnectMessage>() != null, () => d.Pump()), "bad protocol rejected");
        var e = new Fake { C = server.Local.Connect() };
        e.C.Send(new HelloMessage { Name = "bad/name" });
        Check(Wait(() => e.Last<DisconnectMessage>() != null, () => e.Pump()), "bad name rejected");

        // leaving
        b.C.Close("bye");
        Check(Wait(() => a.Last<PlayerLeftMessage>() != null, pump), "leave announced");
        Check(server.PlayerCount == 1, "player count 1");

        server.Stop();
        Console.WriteLine(failures == 0 ? "SERVER ALL OK" : $"{failures} FAILURES");
        return failures;
    }
}
