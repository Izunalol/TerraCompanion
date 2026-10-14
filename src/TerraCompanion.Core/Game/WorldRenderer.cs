using System.Runtime.InteropServices;
using TerraCompanion.Core.Saves;

namespace TerraCompanion.Core.Game;

public sealed class RenderOptions
{
    public bool Tiles = true;
    public bool Walls = true;
    public bool Liquids = true;
    public bool Background = true;
    /// <summary>When set, every tile not in this set is dimmed (used for "find ore" style highlighting).</summary>
    public HashSet<ushort>? Highlight;
}

/// <summary>Renders a world to a BGRA32 (row-major) buffer using the game's own map palette.</summary>
public static class WorldRenderer
{
    public static byte[] Render(WorldFile w, MapColors c, RenderOptions o)
    {
        int W = w.Width, H = w.Height;
        var px = new uint[W * H];

        // Precompute the background colour for every row.
        var bg = new uint[H];
        double surface = w.WorldSurface, rock = w.RockLayer;
        int hell = Math.Max((int)rock + 1, H - 200);
        for (int y = 0; y < H; y++)
        {
            if (!o.Background) bg[y] = 0xFF000000;
            else if (y < surface) bg[y] = Lerp(c.SkyTop, c.SkyBottom, y / surface);
            else if (y < rock) bg[y] = Lerp(c.DirtTop, c.DirtBottom, (y - surface) / Math.Max(1, rock - surface));
            else if (y < hell) bg[y] = Lerp(c.RockTop, c.RockBottom, (y - rock) / Math.Max(1, hell - rock));
            else bg[y] = c.Hell;
        }

        var tiles = w.Tiles; var walls = w.Walls; var liquids = w.Liquids;
        var hl = o.Highlight;
        Parallel.For(0, W, x =>
        {
            int col = x * H;
            for (int y = 0; y < H; y++)
            {
                int i = col + y;
                uint color = bg[y];
                ushort wall = walls[i];
                if (o.Walls && wall != 0) color = wall < c.Wall.Length && c.Wall[wall] != 0 ? c.Wall[wall] : Darken(Fallback(wall + 7919u), 0.5);
                byte liq = liquids[i];
                if (o.Liquids && liq != 0) color = Blend(color, c.Liquid[Math.Min(liq, (byte)4) - 1], 0.75);
                ushort t = tiles[i];
                bool lit = hl == null;
                if (o.Tiles && t != WorldFile.NoTile)
                {
                    color = t < c.Tile.Length && c.Tile[t] != 0 ? c.Tile[t] : Fallback(t);
                    if (hl != null && hl.Contains(t)) { lit = true; color = Brighten(color); }
                }
                if (!lit) color = Darken(Gray(color), 0.35);
                px[y * W + x] = color | 0xFF000000;
            }
        });
        var buffer = new byte[W * H * 4];
        Buffer.BlockCopy(px, 0, buffer, 0, buffer.Length);
        return buffer;
    }

    public static uint Lerp(uint a, uint b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        uint Ch(int s) => (uint)Math.Round(((a >> s) & 0xFF) + ((double)((b >> s) & 0xFF) - ((a >> s) & 0xFF)) * t);
        return 0xFF000000 | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    static uint Blend(uint under, uint over, double alpha) => Lerp(under, over, alpha);

    static uint Darken(uint c, double f) => Lerp(0xFF000000, c, f);
    static uint Brighten(uint c) => Lerp(c, 0xFFFFFFFF, 0.25);

    static uint Gray(uint c)
    {
        uint l = (((c >> 16) & 0xFF) * 30 + ((c >> 8) & 0xFF) * 59 + (c & 0xFF) * 11) / 100;
        return 0xFF000000 | l << 16 | l << 8 | l;
    }

    /// <summary>Stable pseudo-colour for IDs the palette doesn't know.</summary>
    static uint Fallback(uint id)
    {
        uint h = id * 2654435761u;
        return 0xFF000000 | (h & 0x00BFBFBF) + 0x00202020;
    }
}
