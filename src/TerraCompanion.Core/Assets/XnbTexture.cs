using System.Buffers.Binary;
using System.Text;

namespace TerraCompanion.Core.Assets;

/// <summary>A decoded texture: premultiplied BGRA32 pixels (ready for WPF's Pbgra32).</summary>
public sealed class TextureData
{
    public int Width, Height;
    public byte[] Bgra = Array.Empty<byte>();

    /// <summary>Returns a copy cropped to the given rectangle.</summary>
    public TextureData Crop(int x, int y, int w, int h)
    {
        w = Math.Min(w, Width - x); h = Math.Min(h, Height - y);
        var t = new TextureData { Width = w, Height = h, Bgra = new byte[w * h * 4] };
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Bgra, ((y + row) * Width + x) * 4, t.Bgra, row * w * 4, w * 4);
        return t;
    }
}

/// <summary>Reads XNA 4.0 .xnb files containing a Texture2D (compressed or not).</summary>
public static class XnbTexture
{
    public static TextureData Load(string path) => Decode(File.ReadAllBytes(path));

    public static byte[] Decompress(byte[] file)
    {
        if (file.Length < 10 || file[0] != 'X' || file[1] != 'N' || file[2] != 'B')
            throw new InvalidDataException("Not an XNB file.");
        byte flags = file[5];
        int fileSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(6));
        if ((flags & 0x80) == 0) return file.AsSpan(10, fileSize - 10).ToArray();
        if ((flags & 0x40) != 0) throw new NotSupportedException("LZ4-compressed XNB is not supported.");

        int decompressedSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(10));
        var output = new byte[decompressedSize];
        var lzx = new LzxDecoder(16);
        int pos = 14, end = Math.Min(fileSize, file.Length), outPos = 0;
        while (pos < end && outPos < decompressedSize)
        {
            int hi = file[pos], lo = file[pos + 1];
            int blockSize = (hi << 8) | lo, frameSize = 0x8000;
            if (hi == 0xFF)
            {
                frameSize = (lo << 8) | file[pos + 2];
                blockSize = (file[pos + 3] << 8) | file[pos + 4];
                pos += 5;
            }
            else pos += 2;
            if (blockSize == 0 || frameSize == 0) break;
            frameSize = Math.Min(frameSize, decompressedSize - outPos);
            lzx.Decompress(file, pos, blockSize, output, outPos, frameSize);
            pos += blockSize;
            outPos += frameSize;
        }
        if (outPos != decompressedSize) throw new InvalidDataException("XNB: truncated compressed data.");
        return output;
    }

    public static TextureData Decode(byte[] file)
    {
        var d = Decompress(file);
        int p = 0;
        int readers = Read7Bit(d, ref p);
        bool isTexture = false;
        for (int i = 0; i < readers; i++)
        {
            int len = Read7Bit(d, ref p);
            string name = Encoding.UTF8.GetString(d, p, len);
            p += len + 4; // + reader version
            if (name.Contains("Texture2DReader")) isTexture = true;
        }
        if (!isTexture) throw new InvalidDataException("XNB does not contain a Texture2D.");
        Read7Bit(d, ref p); // shared resources
        Read7Bit(d, ref p); // type id of the root object
        int format = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        int w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        int h = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        p += 4; // mip count
        int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        if (format != 0) throw new NotSupportedException($"Texture surface format {format} is not supported.");
        if (size < w * h * 4) throw new InvalidDataException("XNB texture data is too short.");

        // SurfaceFormat.Color is RGBA (already premultiplied by the content pipeline) -> BGRA.
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h * 4; i += 4)
        {
            bgra[i] = d[p + i + 2];
            bgra[i + 1] = d[p + i + 1];
            bgra[i + 2] = d[p + i];
            bgra[i + 3] = d[p + i + 3];
        }
        return new TextureData { Width = w, Height = h, Bgra = bgra };
    }

    static int Read7Bit(byte[] d, ref int p)
    {
        int v = 0, shift = 0;
        byte b;
        do { b = d[p++]; v |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
        return v;
    }
}
