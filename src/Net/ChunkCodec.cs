using System.IO.Compression;
using System.Runtime.InteropServices;
using VoxelGame.Blocks;
using VoxelGame.Terrain;

namespace VoxelGame.Net;

/// <summary>
/// Packs a chunk into bytes for sending to a player, and unpacks it again: blocks, fluid levels and the river flow
/// field (the client can't work that out without the world generator's rivers), all deflate-compressed. Light is not
/// sent: the client works it out itself from the blocks, exactly as the server would.
/// </summary>
public static class ChunkCodec
{
    private const int Cells = Chunk.Size * Chunk.Height * Chunk.Size;

    public static byte[] Encode(Chunk chunk)
    {
        using var output = new MemoryStream(8192);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(MemoryMarshal.AsBytes(chunk.Blocks));
            deflate.Write(chunk.FluidLevels);
            deflate.Write(chunk.FlowCodes);
        }
        return output.ToArray();
    }

    /// <summary>Throws InvalidDataException if the data is damaged or the wrong size.</summary>
    public static Chunk Decode(int chunkX, int chunkZ, byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);

            var blocks = new byte[Cells];
            var fluid = new byte[Cells];
            var flow = new byte[Chunk.Size * Chunk.Size];
            inflate.ReadExactly(blocks);
            inflate.ReadExactly(fluid);
            inflate.ReadExactly(flow);

            var chunk = new Chunk(chunkX, chunkZ);
            chunk.LoadBlocks(MemoryMarshal.Cast<byte, BlockType>(blocks));
            chunk.LoadFluidLevels(fluid);
            chunk.LoadFlowCodes(flow);
            return chunk;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException)
        {
            throw new InvalidDataException($"damaged chunk data for ({chunkX}, {chunkZ}): {e.Message}", e);
        }
    }
}
