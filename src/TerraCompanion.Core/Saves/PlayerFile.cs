using System.Security.Cryptography;
using System.Text;

namespace TerraCompanion.Core.Saves;

/// <summary>One inventory/equipment slot. Stack is implied to be 1 for armor, dye and misc slots.</summary>
public sealed class ItemSlot
{
    public int Type;
    public int Stack;
    public byte Prefix;
    public bool Favorited;

    public bool IsEmpty => Type <= 0;

    public void Clear() { Type = 0; Stack = 0; Prefix = 0; Favorited = false; }

    public void CopyFrom(ItemSlot o) { Type = o.Type; Stack = o.Stack; Prefix = o.Prefix; Favorited = o.Favorited; }
}

public struct Rgb
{
    public byte R, G, B;
    public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
}

public sealed class SpawnPoint
{
    public int X, Y, WorldId;
    public string WorldName = "";
}

/// <summary>
/// Reads and writes Terraria .plr files (releases 230 → 326, i.e. 1.4.0 → 1.4.5).
/// Everything up to the golfer score is parsed into fields; the remainder (journey research,
/// creative powers, loadouts, voice, refunds, dialogue flags) is kept as raw bytes and written
/// back untouched. The serializer uses the same release gates as the game, so an unedited file
/// round-trips byte-for-byte.
/// </summary>
public sealed class PlayerFile
{
    public const int MinRelease = 230;
    public const int KnownMaxRelease = 326;

    static readonly byte[] Key = Encoding.Unicode.GetBytes("h3y_gUyZ");

    public string Path = "";
    public int Release;
    public byte[] Metadata = new byte[20];

    public string Name = "";
    public byte Difficulty;           // 0 classic, 1 mediumcore, 2 hardcore, 3 journey
    public long PlayTimeTicks;
    public int Hair;
    public byte HairDye;
    public byte Team;
    public ushort HideVisibleAccessory; // 10 bits
    public byte HideMisc;
    public byte SkinVariant;
    public int StatLife, StatLifeMax, StatMana, StatManaMax;
    public bool ExtraAccessory, UnlockedBiomeTorches, UsingBiomeTorches, AteArtisanBread, UnusedFlag324;
    public bool UsedAegisCrystal, UsedAegisFruit, UsedArcaneCrystal, UsedGalaxyPearl, UsedGummyWorm, UsedAmbrosia;
    public bool DownedDD2EventAnyDifficulty;
    public int TaxMoney, NumberOfDeathsPVE, NumberOfDeathsPVP;
    public Rgb HairColor, SkinColor, EyeColor, ShirtColor, UnderShirtColor, PantsColor, ShoeColor;

    public ItemSlot[] Armor = NewSlots(20);       // 0-2 armor, 3-9 accessories, 10-19 vanity
    public ItemSlot[] Dye = NewSlots(10);
    public ItemSlot[] Inventory = NewSlots(58);   // 0-49 main, 50-53 coins, 54-57 ammo
    public ItemSlot[] MiscEquips = NewSlots(5);   // pet, light pet, minecart, mount, hook
    public ItemSlot[] MiscDyes = NewSlots(5);
    public ItemSlot[] Bank = NewSlots(40);        // piggy bank
    public ItemSlot[] Bank2 = NewSlots(40);       // safe
    public ItemSlot[] Bank3 = NewSlots(40);       // defender's forge
    public ItemSlot[] Bank4 = NewSlots(40);       // void vault
    public byte VoidVaultInfo;
    public int[] BuffType = new int[44];
    public int[] BuffTime = new int[44];
    public List<SpawnPoint> SpawnPoints = new();
    public bool HbLocked;
    public bool[] HideInfo = new bool[13];
    public int AnglerQuestsFinished;
    public int[] DpadBindings = new int[4];
    public int[] BuilderAccStatus = new int[12];
    public int BartenderQuestLog;
    public bool Dead;
    public int RespawnTimer;
    public long LastSaveTime;
    public int GolferScoreAccumulated;

    /// <summary>Unparsed remainder of the file, written back verbatim.</summary>
    public byte[] Tail = Array.Empty<byte>();

    public int BuffCount => Release >= 252 ? 44 : 22;

    public static string DifficultyName(byte d) => d switch
    {
        0 => "Classic", 1 => "Mediumcore", 2 => "Hardcore", 3 => "Journey", _ => $"Unknown ({d})"
    };

    static ItemSlot[] NewSlots(int n)
    {
        var a = new ItemSlot[n];
        for (int i = 0; i < n; i++) a[i] = new ItemSlot();
        return a;
    }

    // ---------------------------------------------------------------- crypto

    public static byte[] Decrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.DecryptCbc(data, Key, PaddingMode.PKCS7);
    }

    public static byte[] Encrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        return aes.EncryptCbc(data, Key, PaddingMode.PKCS7);
    }

    // ---------------------------------------------------------------- load

    public static PlayerFile Load(string path)
    {
        var p = FromDecrypted(Decrypt(File.ReadAllBytes(path)));
        p.Path = path;
        return p;
    }

    /// <summary>Reads just the name/difficulty/release without keeping the full model around.</summary>
    public static (string name, byte difficulty, int release) Peek(string path)
    {
        var p = Load(path);
        return (p.Name, p.Difficulty, p.Release);
    }

    public static PlayerFile FromDecrypted(byte[] data)
    {
        var p = new PlayerFile();
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        p.Read(r, data.Length);
        return p;
    }

    void Read(BinaryReader r, int length)
    {
        Release = r.ReadInt32();
        if (Release < MinRelease)
            throw new NotSupportedException($"This character was saved by an old Terraria version (release {Release}). Load and save it once in the current game first.");
        if (Release > KnownMaxRelease)
            throw new NotSupportedException($"This character was saved by a newer Terraria version (release {Release}) than this tool understands ({KnownMaxRelease}).");
        Metadata = r.ReadBytes(20);
        int v = Release;

        Name = r.ReadString();
        Difficulty = r.ReadByte();
        PlayTimeTicks = r.ReadInt64();
        Hair = r.ReadInt32();
        HairDye = r.ReadByte();
        if (v >= 283) Team = r.ReadByte();
        HideVisibleAccessory = r.ReadByte();
        HideVisibleAccessory |= (ushort)(r.ReadByte() << 8);
        HideMisc = r.ReadByte();
        SkinVariant = r.ReadByte();
        StatLife = r.ReadInt32();
        StatLifeMax = r.ReadInt32();
        StatMana = r.ReadInt32();
        StatManaMax = r.ReadInt32();
        ExtraAccessory = r.ReadBoolean();
        UnlockedBiomeTorches = r.ReadBoolean();
        UsingBiomeTorches = r.ReadBoolean();
        if (v >= 256) AteArtisanBread = r.ReadBoolean();
        if (v >= 324) UnusedFlag324 = r.ReadBoolean();
        if (v >= 260)
        {
            UsedAegisCrystal = r.ReadBoolean();
            UsedAegisFruit = r.ReadBoolean();
            UsedArcaneCrystal = r.ReadBoolean();
            UsedGalaxyPearl = r.ReadBoolean();
            UsedGummyWorm = r.ReadBoolean();
            UsedAmbrosia = r.ReadBoolean();
        }
        DownedDD2EventAnyDifficulty = r.ReadBoolean();
        TaxMoney = r.ReadInt32();
        if (v >= 254) { NumberOfDeathsPVE = r.ReadInt32(); NumberOfDeathsPVP = r.ReadInt32(); }
        HairColor = ReadRgb(r); SkinColor = ReadRgb(r); EyeColor = ReadRgb(r); ShirtColor = ReadRgb(r);
        UnderShirtColor = ReadRgb(r); PantsColor = ReadRgb(r); ShoeColor = ReadRgb(r);

        foreach (var s in Armor) { s.Type = r.ReadInt32(); s.Prefix = r.ReadByte(); s.Stack = s.Type > 0 ? 1 : 0; if (v >= 322) s.Favorited = r.ReadBoolean(); }
        foreach (var s in Dye) { s.Type = r.ReadInt32(); s.Prefix = r.ReadByte(); s.Stack = s.Type > 0 ? 1 : 0; if (v >= 322) s.Favorited = r.ReadBoolean(); }
        foreach (var s in Inventory) { s.Type = r.ReadInt32(); s.Stack = r.ReadInt32(); s.Prefix = r.ReadByte(); s.Favorited = r.ReadBoolean(); }
        for (int i = 0; i < 5; i++)
        {
            MiscEquips[i].Type = r.ReadInt32(); MiscEquips[i].Prefix = r.ReadByte(); MiscEquips[i].Stack = MiscEquips[i].Type > 0 ? 1 : 0;
            MiscDyes[i].Type = r.ReadInt32(); MiscDyes[i].Prefix = r.ReadByte(); MiscDyes[i].Stack = MiscDyes[i].Type > 0 ? 1 : 0;
        }
        ReadBank(r, Bank, false);
        ReadBank(r, Bank2, false);
        ReadBank(r, Bank3, false);
        ReadBank(r, Bank4, v >= 255);
        VoidVaultInfo = r.ReadByte();

        BuffType = new int[44]; BuffTime = new int[44];
        for (int i = 0; i < BuffCount; i++) { BuffType[i] = r.ReadInt32(); BuffTime[i] = r.ReadInt32(); }

        SpawnPoints.Clear();
        for (int i = 0; i < 200; i++)
        {
            int x = r.ReadInt32();
            if (x == -1) break;
            SpawnPoints.Add(new SpawnPoint { X = x, Y = r.ReadInt32(), WorldId = r.ReadInt32(), WorldName = r.ReadString() });
        }
        HbLocked = r.ReadBoolean();
        for (int i = 0; i < HideInfo.Length; i++) HideInfo[i] = r.ReadBoolean();
        AnglerQuestsFinished = r.ReadInt32();
        for (int i = 0; i < DpadBindings.Length; i++) DpadBindings[i] = r.ReadInt32();
        for (int i = 0; i < BuilderAccStatus.Length; i++) BuilderAccStatus[i] = r.ReadInt32();
        BartenderQuestLog = r.ReadInt32();
        Dead = r.ReadBoolean();
        if (Dead) RespawnTimer = r.ReadInt32();
        LastSaveTime = r.ReadInt64();
        GolferScoreAccumulated = r.ReadInt32();

        int pos = (int)r.BaseStream.Position;
        Tail = r.ReadBytes(length - pos);
    }

    static void ReadBank(BinaryReader r, ItemSlot[] bank, bool favorited)
    {
        foreach (var s in bank)
        {
            s.Type = r.ReadInt32(); s.Stack = r.ReadInt32(); s.Prefix = r.ReadByte();
            if (favorited) s.Favorited = r.ReadBoolean();
        }
    }

    static Rgb ReadRgb(BinaryReader r) => new(r.ReadByte(), r.ReadByte(), r.ReadByte());

    // ---------------------------------------------------------------- save

    public byte[] ToDecrypted()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) Write(w);
        return ms.ToArray();
    }

    void Write(BinaryWriter w)
    {
        int v = Release;
        w.Write(Release);
        w.Write(Metadata);
        w.Write(Name);
        w.Write(Difficulty);
        w.Write(PlayTimeTicks);
        w.Write(Hair);
        w.Write(HairDye);
        if (v >= 283) w.Write(Team);
        w.Write((byte)(HideVisibleAccessory & 0xFF));
        w.Write((byte)(HideVisibleAccessory >> 8));
        w.Write(HideMisc);
        w.Write(SkinVariant);
        w.Write(StatLife); w.Write(StatLifeMax); w.Write(StatMana); w.Write(StatManaMax);
        w.Write(ExtraAccessory); w.Write(UnlockedBiomeTorches); w.Write(UsingBiomeTorches);
        if (v >= 256) w.Write(AteArtisanBread);
        if (v >= 324) w.Write(UnusedFlag324);
        if (v >= 260)
        {
            w.Write(UsedAegisCrystal); w.Write(UsedAegisFruit); w.Write(UsedArcaneCrystal);
            w.Write(UsedGalaxyPearl); w.Write(UsedGummyWorm); w.Write(UsedAmbrosia);
        }
        w.Write(DownedDD2EventAnyDifficulty);
        w.Write(TaxMoney);
        if (v >= 254) { w.Write(NumberOfDeathsPVE); w.Write(NumberOfDeathsPVP); }
        foreach (var c in new[] { HairColor, SkinColor, EyeColor, ShirtColor, UnderShirtColor, PantsColor, ShoeColor })
        { w.Write(c.R); w.Write(c.G); w.Write(c.B); }

        foreach (var s in Armor) { w.Write(SafeType(s)); w.Write(s.Prefix); if (v >= 322) w.Write(s.Favorited); }
        foreach (var s in Dye) { w.Write(SafeType(s)); w.Write(s.Prefix); if (v >= 322) w.Write(s.Favorited); }
        foreach (var s in Inventory) { w.Write(SafeType(s)); w.Write(SafeStack(s)); w.Write(s.Prefix); w.Write(s.Favorited); }
        for (int i = 0; i < 5; i++)
        {
            w.Write(SafeType(MiscEquips[i])); w.Write(MiscEquips[i].Prefix);
            w.Write(SafeType(MiscDyes[i])); w.Write(MiscDyes[i].Prefix);
        }
        WriteBank(w, Bank, false);
        WriteBank(w, Bank2, false);
        WriteBank(w, Bank3, false);
        WriteBank(w, Bank4, v >= 255);
        w.Write(VoidVaultInfo);
        for (int i = 0; i < BuffCount; i++) { w.Write(BuffType[i]); w.Write(BuffTime[i]); }
        foreach (var sp in SpawnPoints.Take(200)) { w.Write(sp.X); w.Write(sp.Y); w.Write(sp.WorldId); w.Write(sp.WorldName); }
        if (SpawnPoints.Count < 200) w.Write(-1);
        w.Write(HbLocked);
        foreach (var b in HideInfo) w.Write(b);
        w.Write(AnglerQuestsFinished);
        foreach (var b in DpadBindings) w.Write(b);
        foreach (var b in BuilderAccStatus) w.Write(b);
        w.Write(BartenderQuestLog);
        w.Write(Dead);
        if (Dead) w.Write(RespawnTimer);
        w.Write(LastSaveTime);
        w.Write(GolferScoreAccumulated);
        w.Write(Tail);
    }

    // An empty slot must be written as type 0 / stack 0; a real item needs a stack of at least 1.
    static int SafeType(ItemSlot s) => s.Type > 0 ? s.Type : 0;
    static int SafeStack(ItemSlot s) => s.Type > 0 ? Math.Max(1, s.Stack) : 0;

    static void WriteBank(BinaryWriter w, ItemSlot[] bank, bool favorited)
    {
        foreach (var s in bank)
        {
            w.Write(SafeType(s)); w.Write(SafeStack(s)); w.Write(s.Prefix);
            if (favorited) w.Write(s.Favorited);
        }
    }

    /// <summary>Writes the character back to <see cref="Path"/> (or another path), after copying the original to <paramref name="backupDir"/>.</summary>
    public string? Save(string? path = null, string? backupDir = null)
    {
        path ??= Path;
        string? backup = null;
        if (backupDir != null && File.Exists(path))
        {
            Directory.CreateDirectory(backupDir);
            backup = System.IO.Path.Combine(backupDir, $"{System.IO.Path.GetFileNameWithoutExtension(path)}_{DateTime.Now:yyyyMMdd_HHmmss}.plr");
            File.Copy(path, backup, overwrite: true);
        }
        var bytes = Encrypt(ToDecrypted());
        // Validate before touching the real file: our own output must parse back.
        FromDecrypted(Decrypt(bytes));
        string tmp = path + ".tctmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        Path = path;
        return backup;
    }
}
