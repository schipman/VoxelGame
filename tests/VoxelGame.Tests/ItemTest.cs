using VoxelGame.Blocks;
using VoxelGame.Items;

static class ItemTest
{
    static int failures;
    static void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }
    static ItemStack S(BlockType b, int n) => new(b, n);

    public static int Run()
    {
        // Transfer
        var a = S(BlockType.Stone, 10); var b = ItemStack.Empty;
        Check(ItemStack.Transfer(ref a, ref b, 4) && a.Count == 6 && b is { Block: BlockType.Stone, Count: 4 }, "partial move onto an empty slot");
        Check(ItemStack.Transfer(ref a, ref b, 6) && a.IsEmpty && b.Count == 10, "moving the rest merges and empties the source");

        a = S(BlockType.Stone, 40); b = S(BlockType.Stone, 40);
        Check(ItemStack.Transfer(ref a, ref b, 40) && b.Count == 64 && a.Count == 16, "merge stops at the stack limit of 64, remainder stays");
        a = S(BlockType.Stone, 5); b = S(BlockType.Stone, 64);
        Check(!ItemStack.Transfer(ref a, ref b, 5) && a.Count == 5 && b.Count == 64, "a full target accepts nothing");

        a = S(BlockType.Stone, 7); b = S(BlockType.Dirt, 3);
        Check(ItemStack.Transfer(ref a, ref b, 7) && a is { Block: BlockType.Dirt, Count: 3 } && b is { Block: BlockType.Stone, Count: 7 }, "whole stack onto a different item swaps");
        a = S(BlockType.Stone, 7); b = S(BlockType.Dirt, 3);
        Check(!ItemStack.Transfer(ref a, ref b, 3) && a.Count == 7 && b.Count == 3, "half a stack onto a different item does nothing");
        var empty = ItemStack.Empty; b = S(BlockType.Dirt, 1);
        Check(!ItemStack.Transfer(ref empty, ref b, 1), "nothing to move from an empty slot");

        // Inventory.Add
        var inv = new Inventory();
        Check(inv.Add(BlockType.Dirt, 100) == 0 && inv[0].Count == 64 && inv[1].Count == 36, "Add fills the hotbar first, 64 per slot");
        Check(inv.Add(BlockType.Dirt, 28) == 0 && inv[1].Count == 64 && inv[2].Count == 0, "Add tops up partial stacks before using empty slots");
        for (int i = 0; i < Inventory.Size; i++) inv[i] = S(BlockType.Stone, 64);
        Check(inv.Add(BlockType.Dirt, 5) == 5, "Add reports what did not fit when full");
        Check(inv.Add(BlockType.Air, 5) == 0 && inv.Remove(0, 64) && inv[0].IsEmpty && !inv.Remove(1, 65), "Remove empties a slot, refuses to take too many");

        // QuickMove
        inv = new Inventory();
        inv[0] = S(BlockType.Stone, 30);
        inv[12] = S(BlockType.Stone, 20);
        inv.QuickMove(0);
        Check(inv[0].IsEmpty && inv[12].Count == 50, "shift-click from the hotbar merges into a matching stack in the main inventory");
        inv.QuickMove(12);
        Check(inv[12].IsEmpty && inv[0].Count == 50, "shift-click from the main inventory goes to the hotbar");

        // Save data
        inv = new Inventory(); inv[3] = S(BlockType.Lamp, 12); inv[20] = S(BlockType.Water, 64);
        var saved = inv.ToSaveData().ToList();
        var back = new Inventory(); back.FromSaveData(saved.Append((5, "NoSuchBlock", 4)).Append((99, "Stone", 1)).Append((6, "Stone", 0)));
        Check(back[3] is { Block: BlockType.Lamp, Count: 12 } && back[20] is { Block: BlockType.Water, Count: 64 } && back[5].IsEmpty && back[6].IsEmpty,
              "save data round-trips by block name and ignores unknown blocks, bad slots and empty counts");

        Console.WriteLine(failures == 0 ? "ITEMS ALL OK" : $"ITEMS FAILED: {failures}");
        return failures;
    }
}
