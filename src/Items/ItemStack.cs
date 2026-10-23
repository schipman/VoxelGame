using VoxelGame.Blocks;

namespace VoxelGame.Items;

/// <summary>
/// A stack of identical items. For now every item is a block, so a stack is a block type and a count.
/// A struct, so inventory arrays hold the values directly and copies can't alias each other.
/// </summary>
public struct ItemStack
{
    public const int MaxCount = 64;

    public BlockType Block;
    public int Count;

    public ItemStack(BlockType block, int count)
    {
        Block = block;
        Count = count;
    }

    public static ItemStack Empty => default;

    public readonly bool IsEmpty => Count <= 0 || Block == BlockType.Air;

    public readonly string Name => IsEmpty ? "" : BlockRegistry.Get(Block).Name;

    /// <summary>
    /// Moves up to <paramref name="amount"/> items from <paramref name="source"/> onto <paramref name="target"/>,
    /// the way dragging does it:
    ///   - onto an empty slot: the items go there;
    ///   - onto the same kind of item: they merge, up to the stack limit;
    ///   - onto a different item: the two swap, but only when the whole stack is being moved.
    /// Returns false if nothing could be moved.
    /// </summary>
    public static bool Transfer(ref ItemStack source, ref ItemStack target, int amount)
    {
        if (source.IsEmpty) return false;
        amount = Math.Min(amount, source.Count);

        if (target.IsEmpty)
        {
            target = new ItemStack(source.Block, amount);
            source.Count -= amount;
        }
        else if (target.Block == source.Block)
        {
            int moved = Math.Min(amount, MaxCount - target.Count);
            if (moved <= 0) return false;
            target.Count += moved;
            source.Count -= moved;
        }
        else if (amount == source.Count)
        {
            (source, target) = (target, source);
        }
        else
        {
            return false;
        }

        if (source.Count <= 0) source = Empty;
        return true;
    }
}
