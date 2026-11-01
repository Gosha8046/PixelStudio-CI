using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using PixelStudio.Core.Imaging;

namespace PixelStudio.Core.IO;

/// <summary>
/// Minimal, dependency-free PNG encoder for 8-bit RGBA (color type 6, non-interlaced).
/// Written in-house to avoid third-party imaging libraries (and their licensing
/// constraints) and because it runs identically on desktop .NET and WebAssembly.
/// Uses the standard per-row adaptive filter heuristic (minimum sum of absolute
/// differences) and zlib via <see cref="ZLibStream"/>.
/// </summary>
public static class PngEncoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Encode(int width, int height, ReadOnlySpan<Color32> pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        if (pixels.Length != width * height)
        {
            throw new ArgumentException("Pixel count does not match the dimensions.", nameof(pixels));
        }

        using var output = new MemoryStream(capacity: 1024 + pixels.Length);
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: truecolor with alpha
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter method: adaptive
        ihdr[12] = 0; // interlace: none
        WriteChunk(output, "IHDR"u8, ihdr);

        WriteChunk(output, "IDAT"u8, CompressScanlines(width, height, MemoryMarshal.AsBytes(pixels)));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static byte[] CompressScanlines(int width, int height, ReadOnlySpan<byte> rgba)
    {
        int stride = width * 4;
        var previous = new byte[stride]; // all zeros for the first row
        var candidates = new byte[5][];
        for (int f = 0; f < candidates.Length; f++)
        {
            candidates[f] = new byte[stride];
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                var row = rgba.Slice(y * stride, stride);
                int best = 0;
                long bestScore = long.MaxValue;
                for (int filter = 0; filter < 5; filter++)
                {
                    long score = ApplyFilter(filter, row, previous, candidates[filter]);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = filter;
                    }
                }

                zlib.WriteByte((byte)best);
                zlib.Write(candidates[best]);
                row.CopyTo(previous);
            }
        }

        return compressed.ToArray();
    }

    /// <summary>Writes the filtered row and returns its "sum of absolute values" score.</summary>
    private static long ApplyFilter(int filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> up, Span<byte> output)
    {
        const int bpp = 4;
        long score = 0;
        for (int i = 0; i < row.Length; i++)
        {
            int x = row[i];
            int a = i >= bpp ? row[i - bpp] : 0;
            int b = up[i];
            int c = i >= bpp ? up[i - bpp] : 0;
            int predicted = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                _ => Paeth(a, b, c),
            };

            byte value = (byte)(x - predicted);
            output[i] = value;
            score += Math.Abs((int)(sbyte)value); // widen first: Math.Abs(sbyte.MinValue) overflows
        }

        return score;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        type.CopyTo(header[4..]);
        stream.Write(header);
        stream.Write(data);

        uint crc = Crc32.Update(0xFFFFFFFFu, type);
        crc = Crc32.Update(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }
}

/// <summary>CRC-32 (ISO-HDLC / PNG polynomial 0xEDB88320), table driven.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Feeds bytes into a running (pre-inverted) CRC value.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
