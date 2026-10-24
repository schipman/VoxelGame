using Microsoft.Xna.Framework;
using VoxelGame.Blocks;
using VoxelGame.Terrain;
static class DebugFall
{
    public static int Run()
    {
        const int Floor = 110;
        var w = new World(null, 5, 2);
        w.Update(Vector3.Zero, int.MaxValue, 0);
        for (int x = -15; x <= 15; x++) for (int z = -15; z <= 15; z++) for (int y = Floor - 3; y <= Floor + 14; y++) w.SetBlock(x, y, z, y <= Floor ? BlockType.Stone : BlockType.Air);
        while (w.Fluids.PendingCount > 0) w.Fluids.Step();
        w.SetBlock(0, Floor + 9, 0, BlockType.Water);
        int t = 0; while (w.Fluids.PendingCount > 0 && t++ < 2000) w.Fluids.Step();
        Console.WriteLine($"settled after {t} ticks");
        Console.Write("column levels y=Floor+1..9: ");
        for (int y = Floor + 1; y <= Floor + 9; y++) Console.Write(w.IsWater(0, y, 0) ? w.GetFluidLevel(0, y, 0) + " " : ". ");
        Console.WriteLine();
        Console.Write("row at floor +x: ");
        for (int x = 0; x <= 9; x++) Console.Write(w.IsWater(x, Floor + 1, 0) ? w.GetFluidLevel(x, Floor + 1, 0) + " " : ". ");
        Console.WriteLine();
        Console.Write("row at floor+2 +x: ");
        for (int x = 0; x <= 9; x++) Console.Write(w.IsWater(x, Floor + 2, 0) ? w.GetFluidLevel(x, Floor + 2, 0) + " " : ". ");
        Console.WriteLine();
        w.SetBlock(0, Floor + 9, 0, BlockType.Air);
        t = 0; while (w.Fluids.PendingCount > 0 && t++ < 2000) w.Fluids.Step();
        Console.WriteLine($"after removing: settled {t} ticks");
        for (int y = Floor + 1; y <= Floor + 9; y++) { int c = 0; for (int x = -15; x <= 15; x++) for (int z = -15; z <= 15; z++) if (w.IsWater(x, y, z)) c++; Console.WriteLine($" y={y - Floor}: {c} water cells"); }
        return 0;
    }
}
