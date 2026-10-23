using VoxelGame.Blocks;

namespace VoxelGame.Items;

/// <summary>
/// The player's items: 36 slots. Slots 0-8 are the hotbar (the row always shown on screen); 9-35 are the main
/// inventory, three rows of nine.
/// </summary>
public sealed class Inventory
{
    public const int HotbarSize = 9;
    public const int Columns = 9;
    public const int Size = 36;

    private readonly ItemStack[] _slots = new ItemStack[Size];

    /// <summary>The hotbar slot (0-8) whose item is held in hand.</summary>
    public int SelectedSlot { get; set; }

    public ItemStack this[int slot]
    {
        get => _slots[slot];
        set => _slots[slot] = value.IsEmpty ? ItemStack.Empty : value;
    }

    public ItemStack Selected => _slots[SelectedSlot];

    public void Clear() => Array.Clear(_slots);

    /// <summary>
    /// Puts items into the inventory: first topping up matching stacks (hotbar first), then using empty slots.
    /// Returns how many did not fit.
    /// </summary>
    public int Add(BlockType block, int count)
    {
        if (block == BlockType.Air) return 0;

        for (int i = 0; i < Size && count > 0; i++)
        {
            if (_slots[i].IsEmpty || _slots[i].Block != block) continue;
            int moved = Math.Min(count, ItemStack.MaxCount - _slots[i].Count);
            _slots[i].Count += moved;
            count -= moved;
        }
        for (int i = 0; i < Size && count > 0; i++)
        {
            if (!_slots[i].IsEmpty) continue;
            int moved = Math.Min(count, ItemStack.MaxCount);
            _slots[i] = new ItemStack(block, moved);
            count -= moved;
        }
        return count;
    }

    /// <summary>Removes items from one slot. Returns false (and changes nothing) if there aren't enough.</summary>
    public bool Remove(int slot, int count)
    {
        if (_slots[slot].Count < count) return false;
        _slots[slot].Count -= count;
        if (_slots[slot].Count <= 0) _slots[slot] = ItemStack.Empty;
        return true;
    }

    /// <summary>Shift-click: sends a stack to the other half of the inventory (hotbar to main, main to hotbar).</summary>
    public void QuickMove(int slot)
    {
        if (_slots[slot].IsEmpty) return;
        bool fromHotbar = slot < HotbarSize;
        int start = fromHotbar ? HotbarSize : 0, end = fromHotbar ? Size : HotbarSize;

        // Merge into matching stacks first, then fill empty slots, like Add but restricted to the other half.
        for (int pass = 0; pass < 2 && !_slots[slot].IsEmpty; pass++)
            for (int i = start; i < end && !_slots[slot].IsEmpty; i++)
            {
                bool wanted = pass == 0 ? (!_slots[i].IsEmpty && _slots[i].Block == _slots[slot].Block) : _slots[i].IsEmpty;
                if (wanted) ItemStack.Transfer(ref _slots[slot], ref _slots[i], _slots[slot].Count);
            }
    }

    // ---- Saving ----

    /// <summary>The non-empty slots as (slot, block name, count), for the level file. Names, not numbers, so saves survive new blocks.</summary>
    public IEnumerable<(int Slot, string Block, int Count)> ToSaveData()
    {
        for (int i = 0; i < Size; i++)
            if (!_slots[i].IsEmpty) yield return (i, _slots[i].Block.ToString(), _slots[i].Count);
    }

    public void FromSaveData(IEnumerable<(int Slot, string Block, int Count)> entries)
    {
        Clear();
        foreach (var (slot, block, count) in entries)
        {
            if (slot < 0 || slot >= Size || count <= 0) continue;
            if (!Enum.TryParse<BlockType>(block, out var type) || type == BlockType.Air) continue;   // a block that no longer exists
            _slots[slot] = new ItemStack(type, Math.Min(count, ItemStack.MaxCount));
        }
    }
}
