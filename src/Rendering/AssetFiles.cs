using Microsoft.Xna.Framework.Graphics;

namespace VoxelGame.Rendering;

/// <summary>
/// Loads editable image files (the block atlas, the water normal map) straight from disk rather than through the
/// content pipeline, so they can be changed and reloaded while the game runs.
/// </summary>
public static class AssetFiles
{
    /// <summary>
    /// Loads a PNG given its path relative to the project, e.g. "Assets/Textures/blocks.png". The working copy in the
    /// project folder is preferred, so edits show up on reload; the copy next to the executable is the fallback.
    /// </summary>
    public static Texture2D LoadTexture(GraphicsDevice device, string relativePath)
    {
        string[] candidates =
        {
            Path.Combine(Directory.GetCurrentDirectory(), relativePath),
            Path.Combine(AppContext.BaseDirectory, relativePath),
        };
        string path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"{relativePath} not found. Looked in:\n  " + string.Join("\n  ", candidates));

        // FileShare.ReadWrite so this works while an image editor still has the file open.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Texture2D.FromStream(device, stream);
    }
}
