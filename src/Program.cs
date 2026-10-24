using VoxelGame;

var options = LaunchOptions.Parse(args);
if (options.ServerPort != null) return ServerHost.Run(options);

using var game = new VoxelGameApp(options);
game.Run();
return 0;
