using VoxelGame.Blocks;
using VoxelGame.Input;
using VoxelGame.Terrain;
using Microsoft.Xna.Framework.Input;

namespace VoxelGame.Entities;

/// <summary>
/// Handles what the player is looking at and lets them break and place blocks.
/// Left mouse breaks, right mouse places the selected block; holding repeats.
/// </summary>
public sealed class BlockInteraction
{
    private const float Reach = 5f;
    private const float RepeatDelay = 0.25f;

    // Every block type except air, so new blocks show up here automatically. This is a testing
    // convenience; the inventory/hotbar will replace it.
    private static readonly BlockType[] PlaceableBlocks =
        Enum.GetValues<BlockType>().Where(t => t != BlockType.Air).ToArray();

    private float _cooldown;
    private int _selected;

    /// <summary>The block under the crosshair, or null if nothing is within reach.</summary>
    public BlockHit? Target { get; private set; }
    public BlockType SelectedBlock => PlaceableBlocks[_selected];

    public void Update(float dt, InputState input, Camera camera, World world, Player player, bool active)
    {
        Target = VoxelRaycast.Cast(world, camera.Position, camera.Forward, Reach);
        if (!active) { _cooldown = 0; return; }

        SelectBlock(input);

        _cooldown -= dt;
        if (!input.LeftHeld && !input.RightHeld) { _cooldown = 0; return; }
        if (_cooldown > 0 || Target is not { } hit) return;

        if (input.LeftHeld) Break(world, hit);
        else Place(world, hit, player);
        _cooldown = RepeatDelay;
    }

    private void SelectBlock(InputState input)
    {
        // 1-9 jump to the first nine blocks; B and the scroll wheel cycle through all of them.
        for (int i = 0; i < Math.Min(9, PlaceableBlocks.Length); i++)
            if (input.WasPressed(Keys.D1 + i)) _selected = i;

        if (input.WasPressed(Keys.B)) CycleSelection(input.IsKeyDown(Keys.LeftShift) ? -1 : 1);   // Shift+B goes backwards
        if (input.ScrollDelta != 0) CycleSelection(-Math.Sign(input.ScrollDelta));
    }

    private void CycleSelection(int step)
    {
        int n = PlaceableBlocks.Length;
        _selected = ((_selected + step) % n + n) % n;
    }

    private static void Break(World world, BlockHit hit)
    {
        if (world.GetBlock(hit.X, hit.Y, hit.Z) == BlockType.Bedrock) return;   // the unbreakable floor
        world.SetBlock(hit.X, hit.Y, hit.Z, BlockType.Air);
    }

    private void Place(World world, BlockHit hit, Player player)
    {
        var (x, y, z) = hit.PlacePosition;
        if (hit.NormalX == 0 && hit.NormalY == 0 && hit.NormalZ == 0) return;   // camera is inside the block
        if (world.GetBlock(x, y, z) != BlockType.Air) return;
        if (player.OverlapsBlock(x, y, z)) return;                              // don't entomb the player
        world.SetBlock(x, y, z, SelectedBlock);
    }
}
