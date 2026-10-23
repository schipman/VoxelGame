using Microsoft.Xna.Framework.Input;
using VoxelGame.Blocks;
using VoxelGame.Input;
using VoxelGame.Items;
using VoxelGame.Settings;
using VoxelGame.Terrain;

namespace VoxelGame.Entities;

/// <summary>
/// Handles what the player is looking at, and breaking and placing blocks with the item in hand.
/// Left mouse breaks, right mouse places the held block; holding repeats.
///   Survival: breaking puts the block in the inventory, placing uses one up.
///   Creative: nothing is collected or used up.
/// Number keys and the scroll wheel choose the hotbar slot.
/// </summary>
public sealed class BlockInteraction
{
    private const float Reach = 5f;
    private const float RepeatDelay = 0.25f;

    // Used by the B shortcut: every block type except air.
    private static readonly BlockType[] AllBlocks = Enum.GetValues<BlockType>().Where(t => t != BlockType.Air).ToArray();

    private readonly Inventory _inventory;
    private float _cooldown;

    /// <summary>The block under the crosshair, or null if nothing is within reach.</summary>
    public BlockHit? Target { get; private set; }

    /// <summary>The block in hand (Air if the slot is empty).</summary>
    public BlockType HeldBlock => _inventory.Selected.IsEmpty ? BlockType.Air : _inventory.Selected.Block;

    public BlockInteraction(Inventory inventory)
    {
        _inventory = inventory;
    }

    /// <param name="active">False while a menu is open or the mouse is not grabbed: only the targeting is updated.</param>
    public void Update(float dt, InputState input, Camera camera, World world, Player player, GameMode mode, bool active)
    {
        Target = VoxelRaycast.Cast(world, camera.Position, camera.Forward, Reach);
        if (!active) { _cooldown = 0; return; }

        SelectSlot(input, mode);

        _cooldown -= dt;
        if (!input.LeftHeld && !input.RightHeld) { _cooldown = 0; return; }
        if (_cooldown > 0 || Target is not { } hit) return;

        if (input.LeftHeld) Break(world, hit, mode);
        else Place(world, hit, player, mode);
        _cooldown = RepeatDelay;
    }

    private void SelectSlot(InputState input, GameMode mode)
    {
        // 1-9 pick a hotbar slot; the scroll wheel steps through them.
        for (int i = 0; i < Inventory.HotbarSize; i++)
            if (input.WasPressed(Keys.D1 + i)) _inventory.SelectedSlot = i;

        if (input.ScrollDelta != 0)
            _inventory.SelectedSlot = ((_inventory.SelectedSlot - Math.Sign(input.ScrollDelta)) % Inventory.HotbarSize + Inventory.HotbarSize) % Inventory.HotbarSize;

        // Creative shortcut for testing: B swaps the held stack for the next block type (Shift+B: previous).
        if (mode == GameMode.Creative && input.WasPressed(Keys.B))
        {
            int step = input.ShiftHeld ? -1 : 1;
            int current = Array.IndexOf(AllBlocks, HeldBlock);
            int next = ((current + step) % AllBlocks.Length + AllBlocks.Length) % AllBlocks.Length;
            _inventory[_inventory.SelectedSlot] = new ItemStack(AllBlocks[next], ItemStack.MaxCount);
        }
    }

    private void Break(World world, BlockHit hit, GameMode mode)
    {
        var block = world.GetBlock(hit.X, hit.Y, hit.Z);
        if (block == BlockType.Bedrock) return;   // the unbreakable floor

        world.SetBlock(hit.X, hit.Y, hit.Z, BlockType.Air);
        if (mode == GameMode.Survival) _inventory.Add(block, 1);
    }

    private void Place(World world, BlockHit hit, Player player, GameMode mode)
    {
        var block = HeldBlock;
        if (block == BlockType.Air) return;                                       // nothing in hand

        var (x, y, z) = hit.PlacePosition;
        if (hit.NormalX == 0 && hit.NormalY == 0 && hit.NormalZ == 0) return;     // camera is inside the block
        if (BlockRegistry.Get(world.GetBlock(x, y, z)).IsSolid) return;           // air and water can be built into
        if (BlockRegistry.Get(block).IsSolid && player.OverlapsBlock(x, y, z)) return;   // don't entomb the player

        world.SetBlock(x, y, z, block);
        if (mode == GameMode.Survival) _inventory.Remove(_inventory.SelectedSlot, 1);
    }
}
