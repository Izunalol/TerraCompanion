using System.Buffers.Binary;
using System.Text;

namespace TerraCompanion.Core.Saves;

public enum Liquid : byte { None = 0, Water = 1, Lava = 2, Honey = 3, Shimmer = 4 }

public sealed class ChestInfo
{
    public int X, Y;
    public string Name = "";
    public List<ItemSlot> Items = new();
}

/// <summary>
/// Read-only parser for Terraria .wld files (release 140+ "version 2" format, verified against 1.4.5).
/// Tiles are decoded into flat column-major arrays (index = x * Height + y), which is the file's own order.
/// </summary>
public sealed class WorldFile
{
    public const ushort NoTile = ushort.MaxValue;

    public string Path = "";
    public int Version;
    public int[] Pointers = Array.Empty<int>();
    public bool[] FrameImportant = Array.Empty<bool>();

    // header
    public string Name = "";
    public string Seed = "";
    public ulong WorldGenVersion;
    public Guid UniqueId;
    public int WorldId;
    public int Width, Height;

    // flags (best effort: anything past the size is wrapped in a try)
    public int GameMode;  // 0 classic, 1 expert, 2 master, 3 journey
    public bool Drunk, GetGood, TenthAnniversary, DontStarve, NotTheBees, Remix, NoTraps, Zenith, Skyblock;
    public DateTime? Created, LastPlayed;
    public int SpawnX, SpawnY;
    public double WorldSurface, RockLayer;
    public int DungeonX, DungeonY;
    public bool Crimson, HardMode;
    public readonly Dictionary<string, bool> Downed = new();
    public string? FlagsError;

    // tiles
    public ushort[] Tiles = Array.Empty<ushort>();
    public ushort[] Walls = Array.Empty<ushort>();
    public byte[] Liquids = Array.Empty<byte>();
    public List<ChestInfo> Chests = new();
    public string? ChestsError;

    public int Index(int x, int y) => x * Height + y;

    public static string GameModeName(int m) => m switch
    {
        0 => "Classic", 1 => "Expert", 2 => "Master", 3 => "Journey", _ => $"Mode {m}"
    };

    public IEnumerable<string> SpecialSeeds()
    {
        if (Drunk) yield return "Drunk world";
        if (GetGood) yield return "For the worthy";
        if (TenthAnniversary) yield return "Celebrationmk10";
        if (DontStarve) yield return "The Constant";
        if (NotTheBees) yield return "Not the bees";
        if (Remix) yield return "Don't dig up";
        if (NoTraps) yield return "No traps";
        if (Zenith) yield return "Zenith (Get fixed boi)";
        if (Skyblock) yield return "Skyblock";
    }

    /// <summary>Reads only the header (fast; used for listing worlds).</summary>
    public static WorldFile LoadHeader(string path) => Load(path, readTiles: false);

    public static WorldFile Load(string path, bool readTiles = true, IProgress<double>? progress = null)
    {
        var data = File.ReadAllBytes(path);
        var w = new WorldFile { Path = path };
        var r = new Reader(data);
        w.ReadFileFormatHeader(ref r);
        r.Pos = w.Pointers[0];
        w.ReadHeader(ref r);
        if (readTiles)
        {
            r.Pos = w.Pointers[1];
            w.ReadTiles(ref r, progress);
            try
            {
                r.Pos = w.Pointers[2];
                w.ReadChests(ref r);
            }
            catch (Exception e) { w.ChestsError = e.Message; }
        }
        return w;
    }

    void ReadFileFormatHeader(ref Reader r)
    {
        Version = r.I32();
        if (Version < 140) throw new NotSupportedException($"World version {Version} is too old to preview.");
        if (Version >= 135)
        {
            ulong magic = r.U64();
            if ((magic & 0x00FFFFFFFFFFFFFFUL) != 0x00_63_69_67_6F_6C_65_72UL) // "relogic"
                throw new InvalidDataException("Not a Terraria world file.");
            int fileType = (int)(magic >> 56);
            if (fileType != 2) throw new InvalidDataException("File is not a world (wrong file type).");
            r.U32(); // revision
            r.U64(); // favorite flags
        }
        int n = r.I16();
        Pointers = new int[n];
        for (int i = 0; i < n; i++) Pointers[i] = r.I32();
        int count = r.U16();
        FrameImportant = new bool[count];
        byte b = 0; int mask = 128;
        for (int i = 0; i < count; i++)
        {
            if (mask == 128) { b = r.U8(); mask = 1; } else mask <<= 1;
            if ((b & mask) == mask) FrameImportant[i] = true;
        }
    }

    void ReadHeader(ref Reader r)
    {
        int v = Version;
        Name = r.Str();
        if (v >= 179)
        {
            Seed = v == 179 ? r.I32().ToString() : r.Str();
            WorldGenVersion = r.U64();
        }
        if (v >= 181) UniqueId = new Guid(r.Bytes(16));
        WorldId = r.I32();
        r.I32(); r.I32(); r.I32(); r.I32(); // left/right/top/bottom (pixels)
        Height = r.I32();
        Width = r.I32();

        try
        {
            if (v >= 209)
            {
                GameMode = r.I32();
                if (v >= 222) Drunk = r.Bool();
                if (v >= 227) GetGood = r.Bool();
                if (v >= 238) TenthAnniversary = r.Bool();
                if (v >= 239) DontStarve = r.Bool();
                if (v >= 241) NotTheBees = r.Bool();
                if (v >= 249) Remix = r.Bool();
                if (v >= 266) NoTraps = r.Bool();
                if (v >= 267) Zenith = r.Bool(); else Zenith = Remix && Drunk;
                if (v >= 302) Skyblock = r.Bool();
            }
            else
            {
                bool expert = v >= 112 && r.Bool();
                bool master = v >= 208 && r.Bool();
                GameMode = master ? 2 : expert ? 1 : 0;
            }
            if (v >= 141) Created = SafeDate(r.I64());
            if (v >= 284) LastPlayed = SafeDate(r.I64());
            r.U8(); // moon type
            for (int i = 0; i < 3; i++) r.I32(); // tree x
            for (int i = 0; i < 4; i++) r.I32(); // tree style
            for (int i = 0; i < 3; i++) r.I32(); // cave back x
            for (int i = 0; i < 4; i++) r.I32(); // cave back style
            r.I32(); r.I32(); r.I32();           // ice / jungle / hell back style
            SpawnX = r.I32(); SpawnY = r.I32();
            WorldSurface = r.F64(); RockLayer = r.F64();
            r.F64(); r.Bool(); r.I32(); r.Bool(); r.Bool(); // time, day, moon phase, blood moon, eclipse
            DungeonX = r.I32(); DungeonY = r.I32();
            Crimson = r.Bool();
            foreach (var boss in new[] { "Eye of Cthulhu", "Eater of Worlds / Brain of Cthulhu", "Skeletron", "Queen Bee",
                                         "The Destroyer", "The Twins", "Skeletron Prime", "Any mechanical boss", "Plantera", "Golem", "King Slime" })
                Downed[boss] = r.Bool();
            foreach (var ev in new[] { "Saved Goblin Tinkerer", "Saved Wizard", "Saved Mechanic", "Goblin Army", "Clown", "Frost Legion", "Pirates" })
                Downed[ev] = r.Bool();
            r.Bool(); r.Bool(); r.U8(); r.I32(); // shadow orb smashed, spawn meteor, orb count, altar count
            HardMode = r.Bool();
        }
        catch (Exception e)
        {
            FlagsError = e.Message;
        }
        if (WorldSurface <= 0 || WorldSurface > Height) WorldSurface = Height * 0.3;
        if (RockLayer <= 0 || RockLayer > Height) RockLayer = WorldSurface + Height * 0.1;
    }

    static DateTime? SafeDate(long bin)
    {
        try { var d = DateTime.FromBinary(bin); return d.Year > 2000 ? d.ToLocalTime() : null; }
        catch { return null; }
    }

    void ReadTiles(ref Reader r, IProgress<double>? progress)
    {
        int w = Width, h = Height;
        if (w <= 0 || h <= 0 || (long)w * h > 200_000_000) throw new InvalidDataException($"Implausible world size {w}x{h}.");
        var tiles = new ushort[w * h];
        var walls = new ushort[w * h];
        var liquids = new byte[w * h];
        var important = FrameImportant;

        for (int x = 0; x < w; x++)
        {
            if ((x & 63) == 0) progress?.Report((double)x / w);
            int col = x * h;
            for (int y = 0; y < h; y++)
            {
                byte h1 = r.U8(), h2 = 0, h3 = 0, h4 = 0;
                if ((h1 & 1) != 0)
                {
                    h2 = r.U8();
                    if ((h2 & 1) != 0)
                    {
                        h3 = r.U8();
                        if ((h3 & 1) != 0) h4 = r.U8();
                    }
                }
                ushort tile = NoTile, wall = 0;
                byte liquid = 0;
                if ((h1 & 2) != 0)
                {
                    int type;
                    if ((h1 & 32) != 0) { int lo = r.U8(); type = (r.U8() << 8) | lo; }
                    else type = r.U8();
                    tile = (ushort)type;
                    if (type < important.Length && important[type]) r.Skip(4); // frameX, frameY
                    if ((h3 & 8) != 0) r.Skip(1);                                 // paint
                }
                if ((h1 & 4) != 0)
                {
                    wall = r.U8();
                    if ((h3 & 16) != 0) r.Skip(1); // wall paint
                }
                int lb = (h1 & 24) >> 3;
                if (lb != 0)
                {
                    byte amount = r.U8();
                    if (amount > 0)
                        liquid = (h3 & 128) != 0 ? (byte)Liquid.Shimmer : (byte)lb; // 1 water, 2 lava, 3 honey
                }
                if (h3 > 1 && (h3 & 64) != 0) wall |= (ushort)(r.U8() << 8);
                // h2 = wires/slopes, h3 = actuators/wire4, h4 = coatings: no extra payload bytes.

                int rle = (h1 & 192) >> 6;
                int k = rle == 0 ? 0 : rle == 1 ? r.U8() : r.I16();
                int idx = col + y;
                tiles[idx] = tile; walls[idx] = wall; liquids[idx] = liquid;
                for (; k > 0 && y + 1 < h; k--)
                {
                    y++; idx++;
                    tiles[idx] = tile; walls[idx] = wall; liquids[idx] = liquid;
                }
            }
        }
        if (r.Pos != Pointers[2])
            throw new InvalidDataException($"Tile data ended at byte {r.Pos} but chests start at {Pointers[2]} - unsupported world format.");
        Tiles = tiles; Walls = walls; Liquids = liquids;
        progress?.Report(1);
    }

    void ReadChests(ref Reader r)
    {
        int count = r.I16();
        int perChest = Version < 294 ? r.I16() : 40;
        for (int i = 0; i < count; i++)
        {
            var c = new ChestInfo { X = r.I32(), Y = r.I32(), Name = r.Str() };
            int slots = perChest;
            if (Version >= 294) slots = r.I32();
            for (int s = 0; s < slots; s++)
            {
                int stack = r.I16();
                if (stack == 0) continue;
                var item = new ItemSlot { Type = r.I32(), Prefix = r.U8(), Stack = stack < 0 ? 1 : stack };
                c.Items.Add(item);
            }
            Chests.Add(c);
        }
    }

    ref struct Reader
    {
        readonly ReadOnlySpan<byte> _d;
        public int Pos;
        public Reader(byte[] d) { _d = d; Pos = 0; }
        public byte U8() => _d[Pos++];
        public bool Bool() => _d[Pos++] != 0;
        public short I16() { var v = BinaryPrimitives.ReadInt16LittleEndian(_d.Slice(Pos)); Pos += 2; return v; }
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d.Slice(Pos)); Pos += 2; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_d.Slice(Pos)); Pos += 4; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d.Slice(Pos)); Pos += 4; return v; }
        public long I64() { var v = BinaryPrimitives.ReadInt64LittleEndian(_d.Slice(Pos)); Pos += 8; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_d.Slice(Pos)); Pos += 8; return v; }
        public double F64() { var v = BinaryPrimitives.ReadDoubleLittleEndian(_d.Slice(Pos)); Pos += 8; return v; }
        public byte[] Bytes(int n) { var b = _d.Slice(Pos, n).ToArray(); Pos += n; return b; }
        public void Skip(int n) => Pos += n;
        public string Str()
        {
            int len = 0, shift = 0;
            byte b;
            do { b = U8(); len |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
            var s = Encoding.UTF8.GetString(_d.Slice(Pos, len));
            Pos += len;
            return s;
        }
    }
}
