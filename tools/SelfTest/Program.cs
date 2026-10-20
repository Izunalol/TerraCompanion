using System.Diagnostics;
using TerraCompanion.Core.Assets;
using TerraCompanion.Core.Saves;

// Usage: SelfTest <command> <args...>
//   players <file.plr...>          round-trip test
//   world <file.wld> [out.png]     parse + optional render (rough palette)
//   xnb <ContentDir> <out.png>     decode all Item_*.xnb, write a contact sheet
int failures = 0;
switch (args[0])
{
    case "players":
        foreach (var f in args.Skip(1))
        {
            try
            {
                var raw = PlayerFile.Decrypt(File.ReadAllBytes(f));
                var p = PlayerFile.FromDecrypted(raw);
                var again = p.ToDecrypted();
                bool same = raw.AsSpan().SequenceEqual(again);
                Console.WriteLine($"{Path.GetFileName(f)}: release {p.Release} name='{p.Name}' diff={PlayerFile.DifficultyName(p.Difficulty)} " +
                                  $"life {p.StatLife}/{p.StatLifeMax} mana {p.StatMana}/{p.StatManaMax} tail={p.Tail.Length}B roundtrip={(same ? "OK" : "MISMATCH")}");
                Console.WriteLine("   inv: " + string.Join(" ", p.Inventory.Where(s => !s.IsEmpty).Take(12).Select(s => $"{s.Type}x{s.Stack}{(s.Prefix > 0 ? "+p" + s.Prefix : "")}{(s.Favorited ? "*" : "")}")));
                Console.WriteLine("   armor: " + string.Join(" ", p.Armor.Select(s => s.Type)) + " | buffs: " + string.Join(" ", Enumerable.Range(0, p.BuffCount).Where(i => p.BuffType[i] > 0).Select(i => $"{p.BuffType[i]}:{p.BuffTime[i]}")));
                Console.WriteLine($"   spawns={p.SpawnPoints.Count} angler={p.AnglerQuestsFinished} golfer={p.GolferScoreAccumulated} lastSave={DateTime.FromBinary(p.LastSaveTime)}");
                if (!same) failures++;
            }
            catch (Exception e) { failures++; Console.WriteLine($"{Path.GetFileName(f)}: FAILED {e.Message}"); }
        }
        break;

    case "world":
    {
        var sw = Stopwatch.StartNew();
        var w = WorldFile.Load(args[1]);
        Console.WriteLine($"'{w.Name}' v{w.Version} seed={w.Seed} {w.Width}x{w.Height} mode={WorldFile.GameModeName(w.GameMode)} hardmode={w.HardMode} crimson={w.Crimson}");
        Console.WriteLine($"  spawn=({w.SpawnX},{w.SpawnY}) dungeon=({w.DungeonX},{w.DungeonY}) surface={w.WorldSurface:F0} rock={w.RockLayer:F0} created={w.Created} played={w.LastPlayed} flagsError={w.FlagsError}");
        Console.WriteLine($"  seeds: {string.Join(", ", w.SpecialSeeds())}; downed: {string.Join(", ", w.Downed.Where(k => k.Value).Select(k => k.Key))}");
        Console.WriteLine($"  chests={w.Chests.Count} (error={w.ChestsError}) items in chests={w.Chests.Sum(c => c.Items.Count)}; parse {sw.ElapsedMilliseconds} ms");
        var hist = w.Tiles.Where(t => t != WorldFile.NoTile).GroupBy(t => t).OrderByDescending(g => g.Count()).Take(8);
        Console.WriteLine("  top tiles: " + string.Join(", ", hist.Select(g => $"{g.Key}:{g.Count()}")));
        if (args.Length > 2)
        {
            var px = new byte[w.Width * w.Height * 4];
            for (int x = 0; x < w.Width; x++)
                for (int y = 0; y < w.Height; y++)
                {
                    int i = w.Index(x, y), o = (y * w.Width + x) * 4;
                    uint c = w.Tiles[i] != WorldFile.NoTile ? Hash(w.Tiles[i]) : w.Liquids[i] != 0 ? 0xFF2040C0 : w.Walls[i] != 0 ? 0xFF303030 : (y < w.WorldSurface ? 0xFF90B0FFu : 0xFF101010u);
                    px[o] = (byte)c; px[o + 1] = (byte)(c >> 8); px[o + 2] = (byte)(c >> 16); px[o + 3] = 255;
                }
            Png.Write(args[2], w.Width, w.Height, px);
        }
        break;
    }

    case "gamedata":
    {
        var sw = Stopwatch.StartNew();
        var g = TerraCompanion.Core.Game.GameData.Load(args[1]);
        Console.WriteLine($"Terraria {g.Version} release {g.Release}, loaded in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"items={g.ItemCount} npcs={g.NpcCount} ({g.NpcNames.Count} named, min {g.NpcNames.Keys.First()}) tiles={g.TileCount} walls={g.WallCount} buffs={g.BuffCount} prefixes={g.PrefixCount} animations={g.ItemAnimationFrames.Count}");
        foreach (int id in new[] { 1, 757, 3063, 4956, 5000, 5738, g.ItemCount - 1 }) Console.WriteLine($"  item {id}: {g.ItemName(id)} ({g.ItemInternalNames[id]})");
        foreach (int id in new[] { -65, 1, 4, 50, 113, 398, g.NpcCount - 1 }) Console.WriteLine($"  npc {id}: {g.NpcName(id)}");
        Console.WriteLine($"  prefix 81: {g.PrefixName(81)}, 59: {g.PrefixName(59)}; buff 290: {g.BuffName(290)}, 86: {g.BuffName(86)}; tile 203: {g.TileName(203)}; wall 1: {g.WallName(1)}");
        Console.WriteLine($"  pet buffs: {g.PetBuffs.Count} e.g. {string.Join(", ", g.PetBuffs.OrderBy(i => i).Take(6).Select(i => g.BuffName(i)))}; light pets: {g.LightPetBuffs.Count}: {string.Join(", ", g.LightPetBuffs.OrderBy(i => i).Select(i => $"{i}={g.BuffName(i)}"))}");
        var m = g.Map;
        Console.WriteLine($"  map colors: tiles {m.TilesFound}/{g.TileCount}, walls {m.WallsFound}/{g.WallCount}; dirt=#{m.Tile[0]:X8} stone=#{m.Tile[1]:X8} grass=#{m.Tile[2]:X8} wall1=#{m.Wall[1]:X8}");
        Console.WriteLine($"  liquids={string.Join(",", m.Liquid.Select(c => c.ToString("X8")))} sky={m.SkyTop:X8}->{m.SkyBottom:X8} dirt={m.DirtTop:X8}->{m.DirtBottom:X8} rock={m.RockTop:X8}->{m.RockBottom:X8} hell={m.Hell:X8}");
        Console.WriteLine($"  missing tile colors: {string.Join(",", Enumerable.Range(0, g.TileCount).Where(i => m.Tile[i] == 0).Take(40))}");
        if (args.Length > 3)
        {
            var w = WorldFile.Load(args[2]);
            var px = TerraCompanion.Core.Game.WorldRenderer.Render(w, m, new TerraCompanion.Core.Game.RenderOptions());
            // downscale 4x for viewing
            int W = w.Width / 4, H = w.Height / 4;
            var small = new byte[W * H * 4];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Buffer.BlockCopy(px, ((y * 4) * w.Width + x * 4) * 4, small, (y * W + x) * 4, 4);
            Png.Write(args[3], W, H, small);
        }
        break;
    }

    case "port64": // port64 <gameDirWithTerrariaExe> <tModLoaderDir> <launcherDir> <outDir>
        Console.WriteLine(TerraCompanion.Core.Game.Port64.Build(args[1], args[2], args[3], args[4]));
        break;

    case "patch": // patch <copyOfTerraria.exe> <hooks.dll>
    {
        Console.WriteLine(TerraCompanion.Core.Game.Patcher.GetStatus(args[1], args[2]));
        Console.WriteLine(TerraCompanion.Core.Game.Patcher.Patch(args[1], args[2]));
        Console.WriteLine(TerraCompanion.Core.Game.Patcher.GetStatus(args[1], args[2]));
        Console.WriteLine("--- patching again (must start from the backup, not double-patch):");
        TerraCompanion.Core.Game.Patcher.Patch(args[1], args[2]);
        using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(args[1]);
        var item = asm.MainModule.GetType("Terraria.Item");
        var sd = item.Methods.First(m => m.Name == "SetDefaults" && m.Parameters.Count == 2);
        int hookCalls = sd.Body.Instructions.Count(i => i.Operand is Mono.Cecil.MethodReference mr && mr.DeclaringType.Name == "GameHooks");
        Console.WriteLine($"Item.SetDefaults hook calls: {hookCalls}; first: {sd.Body.Instructions[0]}; arch={asm.MainModule.Architecture} attrs={asm.MainModule.Attributes}");
        foreach (var i in sd.Body.Instructions.Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ret)) Console.WriteLine($"   {i.Previous.Previous} | {i.Previous} | {i}");
        var spawn = asm.MainModule.GetType("Terraria.NPC").Methods.First(m => m.Name == "SpawnNPC");
        Console.WriteLine("SpawnNPC: " + string.Join(" ; ", spawn.Body.Instructions.Take(4)));
        Console.WriteLine("Win32 resources kept: " + (new FileInfo(args[1]).Length) + " bytes vs original " + new FileInfo(args[1] + ".tcbak").Length);
        break;
    }

    case "edit": // edit <copy.plr> <backupDir> : edit + save + reload a COPY of a character
    {
        var p = PlayerFile.Load(args[1]);
        var originalTail = p.Tail.ToArray();
        var originalName = p.Name;
        p.Name = "Edited Hero";
        p.StatLifeMax = 500; p.StatManaMax = 200; p.UsedAegisFruit = true;
        p.Inventory[49].Type = 757; p.Inventory[49].Stack = 1; p.Inventory[49].Prefix = 81; p.Inventory[49].Favorited = true;
        p.Bank4[0].Type = 3063; p.Bank4[0].Stack = 1;
        p.Inventory[48].Clear();
        int buffSlot = Array.FindIndex(p.BuffType, 0, p.BuffCount, t => t <= 0);
        p.BuffType[buffSlot] = 26; p.BuffTime[buffSlot] = 3600;
        var backup = p.Save(backupDir: args[2]);
        var q = PlayerFile.Load(args[1]);
        bool ok = q.Name == "Edited Hero" && (q.UsedAegisFruit || q.Release < 260) && q.Inventory[49].Type == 757 && q.Inventory[49].Prefix == 81 && q.Inventory[49].Favorited
                  && q.Bank4[0].Type == 3063 && q.Inventory[48].IsEmpty && q.BuffType[buffSlot] == 26 && q.Tail.AsSpan().SequenceEqual(originalTail);
        Console.WriteLine($"edited '{originalName}' -> '{q.Name}', backup={Path.GetFileName(backup)}, verify={(ok ? "OK" : "FAILED")}");
        Console.WriteLine($"   backup still loads as '{PlayerFile.Load(backup!).Name}'; size {new FileInfo(backup!).Length} -> {new FileInfo(args[1]).Length} bytes");
        if (!ok) failures++;

        // rules document: UI syntax and text round trip
        var doc = TerraCompanion.Core.Rules.RulesDocument.Parse("petSlots = 0\nextraAccessories = 6\nnativeCursor = 1\nresponsiveMouse = 1\nvsync = 0\nfpsCap = 144\nminionBonus = 5\nmaxPets = 3\nmaxLightPets = 2\nspawnRate = 2\nstatic Terraria.Item.potionDelay = 1800\nitem 757 damage*=1.5\nitem weapon autoReuse = true\nnpc -3 lifeMax += -5\n# my note\nweird line kept\n");
        var text = doc.ToText();
        var again = TerraCompanion.Core.Rules.RulesDocument.Parse(text);
        bool rok = !again.PetSlots && again.ExtraAccessories == 6 && again.NativeCursor && again.ResponsiveMouse && !again.VSync && again.FpsCap == 144 && again.MinionBonus == 5 && again.MaxPets == 3 && again.MaxLightPets == 2 && again.SpawnRate == 2 && again.Items.Count == 2 && again.Npcs.Count == 1 && again.Npcs[0].Display == "+-5" && again.Other.Contains("weird line kept")
                   && again.Statics["Terraria.Item.potionDelay"] == "1800" && again.ToText() == text;
        TerraCompanion.Core.Rules.FieldRule.TryParseDisplay("x0.5", false, out var op1, out var v1);
        TerraCompanion.Core.Rules.FieldRule.TryParseDisplay("+-10", false, out var op2, out var v2);
        TerraCompanion.Core.Rules.FieldRule.TryParseDisplay("-1", false, out var op3, out var v3);
        rok &= op1 == TerraCompanion.Core.Rules.RuleOp.Multiply && v1 == "0.5" && op2 == TerraCompanion.Core.Rules.RuleOp.Add && v2 == "-10" && op3 == TerraCompanion.Core.Rules.RuleOp.Set && v3 == "-1";
        Console.WriteLine($"rules round trip: {(rok ? "OK" : "FAILED")}");
        Console.Write(text);
        if (!rok) failures++;
        break;
    }

    case "config": failures += ConfigTest.Run(args[1], args[2]); break;

    case "xnb":
    {
        var files = Directory.GetFiles(Path.Combine(args[1], "Images"), "Item_*.xnb");
        int ok = 0;
        var sheet = new byte[64 * 40 * 64 * 40 * 4]; // 64 icons per row, 40px cells -> 2560x2560 for first 4096 items
        var sw = Stopwatch.StartNew();
        foreach (var f in files)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(f).AsSpan(5), out int id)) continue;
            try
            {
                var t = XnbTexture.Load(f);
                ok++;
                if (id < 4096)
                {
                    int cx = (id % 64) * 40, cy = (id / 64) * 40;
                    for (int y = 0; y < Math.Min(40, t.Height); y++)
                        for (int x = 0; x < Math.Min(40, t.Width); x++)
                        {
                            int s = (y * t.Width + x) * 4, d = ((cy + y) * 2560 + cx + x) * 4;
                            sheet[d] = t.Bgra[s]; sheet[d + 1] = t.Bgra[s + 1]; sheet[d + 2] = t.Bgra[s + 2]; sheet[d + 3] = t.Bgra[s + 3];
                        }
                }
            }
            catch (Exception e) { failures++; if (failures < 10) Console.WriteLine($"{Path.GetFileName(f)}: {e.Message}"); }
        }
        Console.WriteLine($"decoded {ok}/{files.Length} item textures in {sw.ElapsedMilliseconds} ms");
        // flatten onto grey so transparent pixels are visible
        for (int i = 0; i < sheet.Length; i += 4)
        {
            int a = sheet[i + 3];
            for (int c = 0; c < 3; c++) sheet[i + c] = (byte)Math.Min(255, sheet[i + c] + (255 - a) * 60 / 255);
            sheet[i + 3] = 255;
        }
        Png.Write(args[2], 2560, 2560, sheet);
        break;
    }
}
return failures == 0 ? 0 : 1;

static uint Hash(ushort t)
{
    uint h = (uint)t * 2654435761u;
    return 0xFF000000 | (h & 0x00FFFFFF);
}

static class ConfigTest
{
    public static int Run(string realConfig, string dir)
    {
        Directory.CreateDirectory(dir);
        var original = File.ReadAllText(realConfig);
        var off = System.Text.RegularExpressions.Regex.Replace(original, @"""Support4K"":\s*true", @"""Support4K"": false");
        File.WriteAllText(Path.Combine(dir, "config.json"), off);
        var before = TerraCompanion.Core.Game.GameConfig.ReadSupport4K(dir);
        bool changed = TerraCompanion.Core.Game.GameConfig.EnableSupport4K(dir);
        var after = TerraCompanion.Core.Game.GameConfig.ReadSupport4K(dir);
        bool again = TerraCompanion.Core.Game.GameConfig.EnableSupport4K(dir);
        var text = File.ReadAllText(Path.Combine(dir, "config.json"));
        bool identical = text == original; // flipping false->true must reproduce the real file exactly
        var res = TerraCompanion.Core.Game.GameConfig.ReadResolution(dir);
        Console.WriteLine($"Support4K before={before} changed={changed} after={after} secondCall={again} restoredExactly={identical} backup={File.Exists(Path.Combine(dir, "config.json.tcbak"))} resolution={res}");
        return before == false && changed && after == true && !again && identical && res == (2560, 1440) ? 0 : 1;
    }
}
