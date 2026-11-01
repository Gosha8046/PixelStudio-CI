using System.Buffers.Binary;
using System.IO.Compression;
using PixelStudio.Core.Imaging;
using PixelStudio.Core.IO;

namespace PixelStudio.Tests.Support;

/// <summary>
/// Strict test-only PNG decoder (8-bit RGBA, non-interlaced) used to verify exporter output:
/// checks the signature, every chunk CRC, IHDR fields and all five filter types.
/// </summary>
internal static class PngTestDecoder
{
    public sealed record Image(int Width, int Height, Color32[] Pixels)
    {
        public Color32 this[int x, int y] => Pixels[y * Width + x];
    }

    public static Image Decode(byte[] png)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(png.AsSpan(0, 8).SequenceEqual(signature), "PNG signature mismatch");

        int offset = 8;
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        bool sawEnd = false;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.Equal(Crc32.Compute(png.AsSpan(offset + 4, length + 4)), crc);

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    Assert.Equal(8, data[8]);
                    Assert.Equal(6, data[9]);
                    Assert.Equal(0, data[12]);
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
            }

            offset += 12 + length;
        }

        Assert.True(sawEnd, "IEND missing");
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();

        int stride = width * 4;
        Assert.Equal(height * (stride + 1), bytes.Length);
        var output = new byte[height * stride];
        for (int y = 0; y < height; y++)
        {
            int filter = bytes[y * (stride + 1)];
            for (int i = 0; i < stride; i++)
            {
                int x = bytes[y * (stride + 1) + 1 + i];
                int a = i >= 4 ? output[y * stride + i - 4] : 0;
                int b = y > 0 ? output[(y - 1) * stride + i] : 0;
                int c = i >= 4 && y > 0 ? output[(y - 1) * stride + i - 4] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Bad filter {filter}"),
                };
                output[y * stride + i] = (byte)(x + predicted);
            }
        }

        var pixels = new Color32[width * height];
        for (int p = 0; p < pixels.Length; p++)
        {
            pixels[p] = new Color32(output[p * 4], output[p * 4 + 1], output[p * 4 + 2], output[p * 4 + 3]);
        }

        return new Image(width, height, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
