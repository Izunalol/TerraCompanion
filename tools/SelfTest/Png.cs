using System.IO.Compression;

/// <summary>Minimal PNG writer (BGRA in, RGBA 8-bit out) for test artifacts.</summary>
static class Png
{
    public static void Write(string path, int w, int h, byte[] bgra)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
        {
            var row = new byte[w * 4 + 1];
            for (int y = 0; y < h; y++)
            {
                row[0] = 0;
                for (int x = 0; x < w; x++)
                {
                    int s = (y * w + x) * 4, d = 1 + x * 4;
                    row[d] = bgra[s + 2]; row[d + 1] = bgra[s + 1]; row[d + 2] = bgra[s]; row[d + 3] = bgra[s + 3];
                }
                z.Write(row);
            }
        }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        data.CopyTo(td, 4);
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, (int)Crc(td)); s.Write(crc);
    }

    static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
