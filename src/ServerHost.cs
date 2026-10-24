using VoxelGame.Persistence;
using VoxelGame.Server;
using VoxelGame.Settings;

namespace VoxelGame;

/// <summary>Runs a server with no window: <c>VoxelGame --server[=PORT] [--seed=N] [--survival] [--distance=N]</c>. Type "stop" (or press Ctrl+C) to save and quit.</summary>
public static class ServerHost
{
    public static int Run(LaunchOptions options)
    {
        int seed = options.Seed ?? Random.Shared.Next();
        int port = options.ServerPort ?? Net.NetDefaults.Port;

        using var server = new GameServer(new ServerOptions
        {
            Seed = seed,
            GameMode = options.Survival ? GameMode.Survival : GameMode.Creative,
            MaxRenderDistance = Math.Clamp(options.Distance ?? 10, 3, 20),
            WorkerThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 6),
        });
        server.Log += line => Console.WriteLine($"[server] {line}");

        server.Start();
        if (server.OpenToNetwork(port) is { } error)
        {
            Console.WriteLine($"Could not start the server: {error}");
            return 1;
        }
        Console.WriteLine($"Server running: world seed {seed}, {server.GameMode}, UDP port {port}.");
        Console.WriteLine("Players can join at: " + string.Join(", ", Net.NetworkAddresses.Local().Select(a => $"{a}:{port}")));
        Console.WriteLine("Type 'stop' to save and quit, 'players' to list who is online.");

        var quit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        var input = new Thread(() =>
        {
            string line;
            while (!quit.IsSet && (line = Console.ReadLine()) != null)
            {
                switch (line.Trim().ToLowerInvariant())
                {
                    case "stop" or "quit" or "exit": quit.Set(); break;
                    case "players": Console.WriteLine($"{server.PlayerCount} player(s) online"); break;
                    case "save": server.RequestSave(); Console.WriteLine("Saving..."); break;
                }
            }
        }) { IsBackground = true };
        input.Start();

        quit.Wait();
        Console.WriteLine("Saving and stopping...");
        return 0;
    }
}
