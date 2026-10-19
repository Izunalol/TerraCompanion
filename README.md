# TerraCompanion

A Windows companion app for **vanilla Terraria** (built and tested against 1.4.5.8). No tModLoader required. It covers:

- **World Rules**: unlock the resolution / zoom cap, extra minion slots and more pets / light pets at once (see below), enemy spawn rate and max enemies, potion-sickness timers, Enchanted Sundial and Moondial cooldowns, and group presets such as weapon damage ×, use speed ×, auto-swing everything, infinite ammo or consumables, and enemy/boss health and damage ×.
- **Item Editor**: per-item overrides for damage, crit, knockback, use time and animation, cooldown, auto-swing, mana, projectile, max stack, "consumed on use", healing, buffs, tool power, defense, value and rarity. Real item icons are included.
- **NPC Editor**: per-NPC life, damage, defense, knockback, coin drops, size and movement flags.
- **World Viewer**: renders any `.wld` with the game's own minimap colors. It supports zoom and pan, depth and compass readout, block/wall/liquid info on hover, highlighting one block type (ores, crystals...), finding items across all chests, and exporting a PNG.
- **Character Editor**: name, difficulty, life and mana, permanent upgrades, colors, inventory, coins and ammo, armor, accessories, vanity and dyes, pets/mounts/hooks, Piggy Bank, Safe, Defender's Forge, Void Vault, and buffs. A timestamped backup is made before every save.
- **Raw Rules**: edit the rules file directly.

## Download

Grab **`TerraCompanion.exe`** from the [latest release](https://github.com/Izunalol/TerraCompanion/releases/latest). It is a single file for 64-bit Windows with nothing to install (no .NET needed). Put it anywhere and run it.

The exe isn't code-signed, so Windows SmartScreen may say "Windows protected your PC" the first time. Click **More info → Run anyway**, or build it yourself from source (below).

## Build from source

Requires the .NET 8 SDK.

```
dotnet build TerraCompanion.sln -c Release
src\TerraCompanion.App\bin\Release\net8.0-windows\TerraCompanion.exe
```

or build the same single-file exe as the release:

```
dotnet publish src\TerraCompanion.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

The hooks DLL that gets installed into the game is embedded in the exe, so `publish\TerraCompanion.exe` is all you need.

## How the "no tModLoader" part works

1. **Game & Patch → Patch game** backs up `Terraria.exe` to `Terraria.exe.tcbak`. It then uses Mono.Cecil to add a few calls into `TerraCompanion.Hooks.dll`:
   - `Item.SetDefaults` / `NPC.SetDefaults`: after the game sets an item or NPC up, your overrides are applied. Prefixes and Expert/Master scaling still work, because overrides run at the right point.
   - `NPC.SpawnNPC`: reloads `rules.txt` within about 0.5 s of a change and applies spawn-rate and static-field rules. Items in your inventory are refreshed in place, so edits show up while you play.
   - `ContentSamples.Initialize`: on first launch, exports vanilla item and NPC stats (`items.tsv`, `npcs.tsv`) so the editors can show the original values.
2. Rules live in `Documents\My Games\Terraria\TerraCompanion\rules.txt`. The hook log is `hooks.log` in the same folder.
3. **Restore original** copies the backup back. Steam's *Verify integrity of game files* also restores the vanilla exe. After a Steam update, the game is vanilla again; press Patch again.

Patching always starts from the clean backup, so re-patching never stacks. A broken rule is logged and skipped; the hooks never throw into the game.

### Rules file syntax

```
spawnRate = 2                              # 2x as many spawns
maxSpawns = 1.5
minionBonus = 5                            # extra minion slots (vanilla 0)
maxPets = 3                                # pets at once (vanilla 1)
maxLightPets = 2                           # light pets at once (vanilla 1)
sundialCooldown = 2                        # Enchanted Sundial cooldown in days (vanilla 8)
moondialCooldown = 0                       # Enchanted Moondial: usable every night
static Terraria.Item.potionDelay = 1800    # any static field (ticks: 60 = 1 s)
static Terraria.Main.SupportWideScreen = true   # unlock the resolution / zoom cap
item 757 damage = 300                      # set
item 757 useTime *= 0.5                    # multiply
item * tileBoost += 3                      # add
item ammo consumable = false               # selectors: id, *, stackable, consumable, weapon, ammo, accessory, tool
npc boss lifeMax *= 2                      # selectors: id (negatives too), *, boss, hostile, friendly, town
```

Group rules run first, then id rules, so an id rule always wins. Fields are any public number or true/false field on `Terraria.Item` / `Terraria.NPC`.

## Notes and limits

- **Minions and pets:** `minionBonus` raises the base minion count (vanilla 1) that armor and accessories add to. Vanilla removes your old pet whenever you get a new one; `maxPets` / `maxLightPets` let several stay out (the oldest go once you pass the limit). Extra pets are summoned the normal way and live on as buffs, so they persist with the character; the Character Editor can also add them under Buffs. The equipment slots themselves stay at five (pet, light pet, minecart, mount, hook) because the character file format fixes that number.
- **Appearance:** the app follows the Windows light / dark setting by default (Apple-style Light and Dark palettes); change it with the Appearance menu in the sidebar.

- **Resolution / zoom cap:** Terraria forces a minimum zoom of `max(1, width/1920, height/1200)`, so above 1080p the view is only magnified and you never see more world. The **Display** option on World Rules sets the game's own hidden `Main.SupportWideScreen` switch, removing that cap (render targets up to 4096 px). It also turns on the game's `Support4K` option in `config.json` (backup kept). Restart the game after changing it. Rain, falling stars and enemy-spawn areas still use the vanilla 1920x1200 region.

- **Spawn rate baseline:** 1× in the spawn sliders is always true vanilla (spawn delay 600, max enemies 5), even if your `Terraria.exe` was hand-edited to spawn faster. The World Rules page warns when it detects that.

- **Multiplayer:** rules only affect the game where the patch is installed. Item and NPC changes count in multiplayer only if the host or dedicated server is patched with the same rules (the Game page can also patch `TerrariaServer.exe`).
- **Character editing:** edit characters while they are not loaded in game, or the game overwrites the file on its next save. Steam Cloud characters are listed but may be re-synced by Steam. Saves from release 230 (1.4.0) up to 326 (1.4.5) are supported. Older saves are written back in their own format, and the game upgrades them on load. Only the active equipment loadout is editable; the other two are preserved.
- In 1.4.5 the game caps max life at 500 and max mana at 200 when loading a character.
- The World Viewer is read-only.

## Project layout

| Path | What |
|---|---|
| `src/TerraCompanion.Core` | Save parsers (`PlayerFile`, `WorldFile`), LZX/XNB texture decoder, map-palette extractor, `GameData` (IDs and names read from Terraria.exe), patcher, rules model |
| `src/TerraCompanion.Hooks` | .NET Framework 4.0 DLL loaded by the patched game (reflection only, no game reference) |
| `src/TerraCompanion.App` | WPF app |
| `tools/SelfTest` | Developer checks: `players`, `world`, `gamedata`, `xnb`, `patch`, `edit` (run against copies of your files) |
| `tools/IconGen` | Draws the pixel-art tree app icon (`dotnet run --project tools/IconGen -- <outDir>`) |

## License

[MIT](LICENSE). Not affiliated with or endorsed by Re-Logic; Terraria is a trademark of Re-Logic.
