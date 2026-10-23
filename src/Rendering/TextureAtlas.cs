using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using VoxelGame.Blocks;

namespace VoxelGame.Rendering;

/// <summary>
/// The block texture atlas: one PNG (Assets/Textures/blocks.png) holding every block face texture in a
/// 16x16 grid of tiles. A tile's position comes from its number in the <see cref="Tile"/> enum, counted
/// left to right then top to bottom: column = number % 16, row = number / 16.
/// The tile size in pixels is the image width / 16, so the whole atlas can be redrawn at 32x32 or 64x64 per tile.
/// </summary>
public static class TextureAtlas
{
    public const int TilesPerRow = 16;

    // Pulls UVs a hair inside each tile so neighbouring tiles never bleed in.
    private const float Inset = 0.0005f;

    private const string RelativePath = "Assets/Textures/blocks.png";

    /// <summary>UV rectangle (min corner and size) of a tile.</summary>
    public static (Vector2 Min, float Size) GetTileUV(Tile tile)
    {
        int i = (int)tile;
        float s = 1f / TilesPerRow;
        return (new Vector2(i % TilesPerRow * s, i / TilesPerRow * s), s);
    }

    /// <summary>Maps a unit (0..1) coordinate within a tile to atlas UV space.</summary>
    public static Vector2 MapUV(Tile tile, Vector2 unit)
    {
        var (min, size) = GetTileUV(tile);
        return min + (new Vector2(Inset) + unit * (1f - 2f * Inset)) * size;
    }

    /// <summary>Loads the atlas from disk (see <see cref="AssetFiles"/>) and checks it is a sensible size.</summary>
    public static Texture2D Load(GraphicsDevice device)
    {
        var texture = AssetFiles.LoadTexture(device, RelativePath);

        if (texture.Width != texture.Height || texture.Width % TilesPerRow != 0)
        {
            int w = texture.Width, h = texture.Height;
            texture.Dispose();
            throw new InvalidDataException(
                $"{RelativePath} is {w}x{h}; the atlas must be square with a width divisible by {TilesPerRow} (e.g. 256x256).");
        }
        return texture;
    }
}
