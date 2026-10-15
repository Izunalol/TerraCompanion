// Draws the TerraCompanion icon: an original pixel-art tree in the style of a Terraria forest tree.
// Usage: IconGen <outDir>   ->  TerraCompanion.ico, icon_<size>.png, preview.png
//
// The tree is described with simple shapes in a 32x32 design space and rasterised on a pixel grid
// (16, 24 or 32 cells), then scaled up by whole numbers so the pixels stay crisp at every size.

string outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

// grid size used for each icon size (larger icons are integer upscales of a grid)
var plan = new (int size, int grid)[] { (16, 16), (24, 24), (32, 32), (48, 24), (64, 32), (128, 32), (256, 32) };
var entries = new List<(int size, byte[] png)>();
foreach (var (size, grid) in plan)
{
    var cells = Tree.Render(grid);
    var px = Upscale(cells, grid, size / grid);
    string path = Path.Combine(outDir, $"icon_{size}.png");
    Png.Write(path, size, size, px);
    entries.Add((size, File.ReadAllBytes(path)));
}
WriteIco(Path.Combine(outDir, "TerraCompanion.ico"), entries);

// preview sheet: every size side by side on dark and light backgrounds
{
    int w = entries.Sum(e => e.size + 16) + 16, h = 2 * (256 + 16) + 16;
    var sheet = new byte[w * h * 4];
    for (int i = 0; i < w * h; i++) { bool light = i / w > h / 2; sheet[i * 4] = sheet[i * 4 + 1] = sheet[i * 4 + 2] = (byte)(light ? 0xEE : 0x1D); sheet[i * 4 + 3] = 255; }
    int x0 = 16;
    foreach (var (size, grid) in plan)
    {
        var px = Upscale(Tree.Render(grid), grid, size / grid);
        foreach (int y0 in new[] { 16, 256 + 32 })
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int s = (y * size + x) * 4, d = ((y0 + y) * w + x0 + x) * 4;
                    int a = px[s + 3];
                    for (int c = 0; c < 3; c++) sheet[d + c] = (byte)((px[s + c] * a + sheet[d + c] * (255 - a)) / 255);
                }
        x0 += size + 16;
    }
    Png.Write(Path.Combine(outDir, "preview.png"), w, h, sheet);
}
Console.WriteLine($"Wrote {entries.Count} sizes to {outDir}");

static byte[] Upscale(uint[] cells, int grid, int k)
{
    int size = grid * k;
    var px = new byte[size * size * 4];
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            uint c = cells[(y / k) * grid + x / k];
            int o = (y * size + x) * 4;
            px[o] = (byte)c; px[o + 1] = (byte)(c >> 8); px[o + 2] = (byte)(c >> 16); px[o + 3] = (byte)(c >> 24);
        }
    return px;
}

static void WriteIco(string path, List<(int size, byte[] png)> images)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
}

static class Tree
{
    // palette (ARGB)
    const uint Clear = 0x00000000;
    const uint LeafOutline = 0xFF143A18, LeafDark = 0xFF2A6A24, LeafMid = 0xFF3F9A33, LeafLight = 0xFF66C04A, LeafShine = 0xFFA6E270;
    const uint BarkOutline = 0xFF2B190E, BarkDark = 0xFF5A351A, BarkMid = 0xFF7E4C26, BarkLight = 0xFFA66B3C;
    const uint GrassDark = 0xFF2B6E21, GrassLight = 0xFF57B23E;

    // canopy = union of leaf clumps (cx, cy, r) in 32x32 design space; each clump is shaded on its own,
    // which gives the bumpy, clustered look of Terraria tree tops
    static readonly (double x, double y, double r)[] Lobes =
    {
        // rim
        (16, 5.0, 4.4), (10.6, 6.4, 4.2), (21.4, 6.4, 4.2), (6.9, 10.6, 3.9), (25.1, 10.6, 3.9),
        (7.9, 15.0, 3.8), (24.1, 15.0, 3.8), (12.4, 17.2, 3.6), (19.6, 17.2, 3.6),
        // fill
        (12.6, 10.8, 4.3), (19.4, 10.8, 4.3), (16, 14.2, 4.2), (16, 8.8, 3.8),
        // side tuft on the branch
        (9.2, 22.2, 2.1),
    };

    public static uint[] Render(int grid)
    {
        double s = 32.0 / grid;
        var kind = new byte[grid * grid];   // 0 empty, 1 leaf, 2 bark, 3 grass
        var col = new uint[grid * grid];
        for (int gy = 0; gy < grid; gy++)
            for (int gx = 0; gx < grid; gx++)
            {
                double x = (gx + 0.5) * s, y = (gy + 0.5) * s;
                int i = gy * grid + gx;
                if (Leaf(x, y, out double shade)) { kind[i] = 1; col[i] = LeafColor(shade, gx, gy); }
                else if (Grass(x, y)) { kind[i] = 3; col[i] = ((gx + gy) & 1) == 0 || y < 28.6 ? GrassLight : GrassDark; }
                else if (Bark(x, y, out double t)) { kind[i] = 2; col[i] = BarkColor(t, y, gx, grid); }
            }
        // outlines: a filled cell next to an empty one (4-neighbourhood) becomes the darkest tone of its material
        var outCol = (uint[])col.Clone();
        for (int gy = 0; gy < grid; gy++)
            for (int gx = 0; gx < grid; gx++)
            {
                int i = gy * grid + gx;
                if (kind[i] == 0) { outCol[i] = Clear; continue; }
                bool edge = gx == 0 || gy == 0 || gx == grid - 1 || gy == grid - 1
                            || kind[i - 1] == 0 || kind[i + 1] == 0 || kind[i - grid] == 0 || kind[i + grid] == 0;
                // leaves also get a dark rim where they overhang the trunk
                bool overTrunk = kind[i] == 1 && gy + 1 < grid && kind[i + grid] == 2;
                if (edge || overTrunk)
                    outCol[i] = kind[i] switch { 1 => LeafOutline, 2 => BarkOutline, _ => GrassDark };
            }
        return outCol;
    }

    static bool Leaf(double x, double y, out double shade)
    {
        shade = 0;
        double best = -1;
        foreach (var (cx, cy, r) in Lobes)
        {
            double dx = x - cx, dy = y - cy, d = Math.Sqrt(dx * dx + dy * dy);
            if (d > r || (r - d) / r <= best) continue;
            // shade by the lobe this pixel sits deepest inside; light comes from the top-left,
            // and the lower canopy is a little darker overall
            best = (r - d) / r;
            shade = -(dx / r * 0.62 + dy / r * 0.78) + 0.3 * best - 0.32 * (y - 10) / 10;
        }
        return best >= 0;
    }

    static uint LeafColor(double shade, int gx, int gy)
    {
        bool checker = ((gx + gy) & 1) == 0;
        if (shade > 0.62) return checker ? LeafShine : LeafLight;
        if (shade > 0.42) return LeafLight;
        if (shade > 0.30) return checker ? LeafLight : LeafMid;
        if (shade > -0.05) return LeafMid;
        if (shade > -0.20) return checker ? LeafMid : LeafDark;
        return LeafDark;
    }

    static double TrunkHalfWidth(double y) => y < 25 ? 2.7 : 2.7 + (y - 25) * 0.85;

    static bool Bark(double x, double y, out double t)
    {
        t = 0.5;
        // short branch out to the side tuft
        if (y >= 21.0 && y <= 24.0 && x >= 10.0 && x <= 14) return true;
        if (y < 14 || y > 29.6) return false;
        double w = TrunkHalfWidth(y);
        if (Math.Abs(x - 16) > w) return false;
        t = (x - (16 - w)) / (2 * w); // 0 = left edge, 1 = right edge
        return true;
    }

    static uint BarkColor(double t, double y, int gx, int grid)
    {
        if (t < 0.30) return BarkLight;
        // a broken vertical groove, like Terraria's bark
        bool groove = t > 0.55 && t < 0.72 && ((int)y % 6) != 2;
        if (groove) return BarkDark;
        return t > 0.80 ? BarkDark : BarkMid;
    }

    static bool Grass(double x, double y)
    {
        if (x < 8 || x > 24 || y > 30.4) return false;
        // a low mound with a few blades poking up
        double mound = 28.8 + Math.Pow((x - 16) / 8.0, 2) * 0.9;
        bool blade = (int)x is 11 or 14 or 19;
        return y >= mound - (blade ? 1.0 : 0);
    }
}
