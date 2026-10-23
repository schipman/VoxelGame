using System.IO.Compression;
using System.Text.Json;
using VoxelGame.Blocks;
using VoxelGame.Terrain;

namespace VoxelGame.Persistence;

/// <summary>
/// Reads and writes one world's save folder:
///   level.json              seed, time of day, player position
///   chunks/c.{x}.{z}.bin    one file per chunk the player has changed
/// Only changed chunks are stored. Untouched terrain is regenerated from the seed, which keeps saves tiny.
/// </summary>
public sealed class WorldStorage
{
    private const int ChunkFileVersion = 2;   // 2 added the fluid levels after the blocks

    /// <summary>Where all worlds live: %AppData%\VoxelGame\saves. Each world gets its own folder named after its seed.</summary>
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoxelGame", "saves");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _chunkDirectory;
    private readonly string _levelPath;

    public string Directory { get; }

    /// <summary>True if this world has been saved before.</summary>
    public bool Exists => File.Exists(_levelPath);

    public WorldStorage(string root, int seed)
    {
        Directory = Path.Combine(root, $"seed_{seed}");
        _chunkDirectory = Path.Combine(Directory, "chunks");
        _levelPath = Path.Combine(Directory, "level.json");
    }

    // ---- Level ----

    public LevelData LoadLevel()
    {
        if (!Exists) return null;
        try
        {
            return JsonSerializer.Deserialize<LevelData>(File.ReadAllText(_levelPath));
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Console.WriteLine($"Could not read {_levelPath}: {e.Message}");
            return null;
        }
    }

    public void SaveLevel(LevelData level)
    {
        System.IO.Directory.CreateDirectory(Directory);
        WriteAtomically(_levelPath, stream => JsonSerializer.Serialize(stream, level, JsonOptions));
    }

    // ---- Chunks ----

    /// <summary>
    /// Fills the chunk from its save file. Returns false if the chunk was never saved,
    /// in which case the caller should generate it from the seed instead.
    /// </summary>
    public bool TryLoadChunk(Chunk chunk)
    {
        string path = ChunkPath(chunk.ChunkX, chunk.ChunkZ);
        if (!File.Exists(path)) return false;

        try
        {
            using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);

            int version = reader.ReadInt32();
            if (version is < 1 or > ChunkFileVersion) throw new InvalidDataException($"unknown chunk file version {version}");

            // The palette lists block names, so saves survive reordering or adding BlockType entries.
            int paletteSize = reader.ReadByte();
            var palette = new BlockType[paletteSize];
            for (int i = 0; i < paletteSize; i++)
            {
                string name = reader.ReadString();
                palette[i] = Enum.TryParse<BlockType>(name, out var type) ? type : BlockType.Air;
            }

            var indices = new byte[Chunk.Size * Chunk.Height * Chunk.Size];
            reader.BaseStream.ReadExactly(indices);

            var blocks = new BlockType[indices.Length];
            for (int i = 0; i < indices.Length; i++) blocks[i] = palette[indices[i]];
            chunk.LoadBlocks(blocks);

            if (version >= 2)
            {
                var fluid = new byte[indices.Length];
                reader.BaseStream.ReadExactly(fluid);
                chunk.LoadFluidLevels(fluid);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or EndOfStreamException)
        {
            // Keep the broken file for inspection rather than silently overwriting it, and regenerate.
            Console.WriteLine($"Chunk file {path} is unreadable ({e.Message}); moving it aside.");
            File.Move(path, path + ".corrupt", overwrite: true);
            return false;
        }
    }

    public void SaveChunk(Chunk chunk)
    {
        System.IO.Directory.CreateDirectory(_chunkDirectory);

        // Palette: every distinct block type in this chunk, so each block is stored as a small index.
        var paletteIndex = new Dictionary<BlockType, byte>();
        var palette = new List<BlockType>();
        var blocks = chunk.Blocks;
        var indices = new byte[blocks.Length];
        for (int i = 0; i < blocks.Length; i++)
        {
            if (!paletteIndex.TryGetValue(blocks[i], out byte index))
            {
                index = (byte)palette.Count;
                paletteIndex[blocks[i]] = index;
                palette.Add(blocks[i]);
            }
            indices[i] = index;
        }

        WriteAtomically(ChunkPath(chunk.ChunkX, chunk.ChunkZ), stream =>
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            using var writer = new BinaryWriter(gzip);
            writer.Write(ChunkFileVersion);
            writer.Write((byte)palette.Count);
            foreach (var type in palette) writer.Write(type.ToString());
            writer.Write(indices);
            writer.Write(chunk.FluidLevels);
        });
    }

    private string ChunkPath(int chunkX, int chunkZ) => Path.Combine(_chunkDirectory, $"c.{chunkX}.{chunkZ}.bin");

    /// <summary>Writes to a temporary file and renames it, so a crash mid-write can't leave a half-written save.</summary>
    private static void WriteAtomically(string path, Action<Stream> write)
    {
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
            write(stream);
        File.Move(temp, path, overwrite: true);
    }
}
