using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace TerraCompanion.Hooks
{
    /// <summary>
    /// Extra accessory slots, each a [functional | vanity | dye] triple, on the accessory page of the inventory.
    ///
    /// Like the extra pet slots, they live in our own item arrays (never in Player.armor, whose size is baked into the
    /// character file and the multiplayer sync) and are drawn and clicked with the game's own ItemSlot code. Their
    /// contents are then fed into the game's own accessory pipeline, each at the point where vanilla does it:
    ///   effects   (UpdateEquips):  Grant*Benefits + ApplyEquipFunctional  after the vanilla functional loop
    ///   vanity    (UpdateEquips):  ApplyEquipVanity                       after the vanilla vanity loop
    ///   dyes      (UpdateDyes):    UpdateItemDye                          around the vanilla dye loop
    ///   visuals   (PlayerFrame):   UpdateVisibleAccessory                 around UpdateVisibleAccessories
    /// "Before" for functional and "after" for vanity keeps vanilla's priorities: a vanilla vanity item still beats an
    /// extra functional one, and an extra vanity item beats everything.
    ///
    /// The arrays are shaped like Player.armor (functional items at index 3.., vanity at 13..) so the game's own
    /// "may this item go in this slot" rules apply; five slots per array, using indices 3..7 / 13..17, which avoids the
    /// indices (8, 9, 18, 19) the game treats as conditionally unlocked.
    /// On top of that, the game's accessory-compatibility rule (no duplicates, no two wings...) is applied across
    /// vanilla and extra slots together.
    ///
    /// Every entry point swallows exceptions and switches the feature off after repeated errors.
    /// </summary>
    static class ExtraAccessories
    {
        const int MaxExtra = 14;
        const int PerArray = 5;          // slots per armor-shaped array
        const int RowsPerColumn = 7;     // visible accessory rows (3..9) on the page
        const int ColumnStep = 47;
        const int GapFromVanilla = 6;
        const int InventoryRightEdge = 600;
        const int AccessoryContext = 10, VanityContext = 11, DyeContext = 12;

        static bool _init, _dead;
        static int _failures;

        static Assembly _game;
        static Type _itemT;
        static MethodInfo _draw, _handle, _setDefaults, _prefix, _grantPrefix, _grantArmor, _applyFunctional, _applyVanity, _updateDye,
                          _visible, _visuallyIncompatible, _canBoth, _expertGetter;
        static FieldInfo _mouseX, _mouseY, _scale, _spriteBatch, _armorHide, _myPlayer, _players, _hoverName, _activeFile, _invBack;
        static FieldInfo _fType, _fStack, _fPrefix, _fAccessory, _fExpertOnly, _fWingSlot, _fMouseInterface, _pArmor, _pHide, _pWings, _pWho;
        static PropertyInfo _ignoreMouse, _assetValue, _texWidth, _filePath;
        static Type _vec2T, _colorT;

        // The little eye on each functional slot: hides that accessory's look (effects stay), like the vanilla toggle.
        static bool[] _hidden = new bool[MaxExtra];
        static bool _tickOk;
        static FieldInfo _tickOn, _tickOff, _mouseLeft, _mouseLeftRelease, _mouseItem, _pVelocity, _langInter;
        static MethodInfo _clearHover, _playSound, _spriteBatchDraw;
        static PropertyInfo _texHeight, _textValue;

        static Array[] _acc;   // [array] -> Item[20]: functional at 3..7, vanity at 13..17
        static Array[] _dyes;  // [array] -> Item[10]: dye at 3..7 (shared by that row's functional and vanity item)
        static string _loadedFor, _lastSaved = "";

        // ------------------------------------------------------------------ entry points (called from patched game code)

        /// <summary>End of the accessory-page slot loop in Main.DrawInventory. baseY = the page's top (the game's own local).</summary>
        public static void Draw(int baseY)
        {
            try
            {
                int count;
                if (!Active(out count)) return;
                object player = LocalPlayer();
                if (player == null) return;
                SyncWithCharacter();

                int size = SlotSize();
                float scale = (float)_scale.GetValue(null);
                int x0 = (int)Math.Round(ScreenWidth() - 92.0);
                int maxColumns = Math.Max(0, (x0 - InventoryRightEdge) / (3 * ColumnStep));
                for (int k = 0; k < count; k++)
                {
                    int column = k / RowsPerColumn, row = 3 + k % RowsPerColumn;
                    if (column >= maxColumns) break;
                    int y = baseY + (int)(row * 56 * scale) + (row > 2 ? 4 : 0);
                    int fx = x0 - (3 + 3 * column) * ColumnStep - GapFromVanilla;
                    int a = k / PerArray, j = k % PerArray;
                    bool onEye = OverToggle(k, fx + 34, y - 2); // like vanilla, the eye wins over the slot beneath it
                    Slot(player, _acc[a], AccessoryContext, 3 + j, fx, y, size, k, false, onEye);
                    Toggle(k, fx + 34, y - 2);
                    Slot(player, _acc[a], VanityContext, 13 + j, fx - ColumnStep, y, size, k, true);
                    Slot(player, _dyes[a], DyeContext, 3 + j, fx - 2 * ColumnStep, y, size, k, false);
                }
                SaveIfChanged();
            }
            catch (Exception e) { Fail("draw", e); }
        }

        /// <summary>After the vanilla functional-accessory loop in Player.UpdateEquips.</summary>
        public static void Functional(object player)
        {
            try
            {
                int count;
                if (!Active(out count) || !IsLocal(player)) return;
                SyncWithCharacter();
                bool[] hide = (bool[])_pHide.GetValue(player);
                bool saved = hide[3];
                try
                {
                    foreach (int k in Functionals(player, count))
                    {
                        object item = Get(_acc, k, 3);
                        hide[3] = _hidden[k]; // ApplyEquipFunctional only uses this flag, just as for a vanilla slot
                        _grantPrefix.Invoke(player, new object[] { item });
                        _grantArmor.Invoke(player, new object[] { item });
                        _applyFunctional.Invoke(player, new object[] { 3, item });
                    }
                }
                finally { hide[3] = saved; }
            }
            catch (Exception e) { Fail("effects", e); }
        }

        /// <summary>After the vanilla vanity loop in Player.UpdateEquips.</summary>
        public static void Vanity(object player)
        {
            try
            {
                int count;
                if (!Active(out count) || !IsLocal(player)) return;
                foreach (object item in Vanities(count)) _applyVanity.Invoke(player, new object[] { 13, item });
            }
            catch (Exception e) { Fail("vanity", e); }
        }

        /// <summary>Inside Player.UpdateDyes, before the vanilla loop (so vanilla items keep priority).</summary>
        public static void DyesFunctional(object player)
        {
            try
            {
                int count;
                if (!Active(out count) || !IsLocal(player)) return;
                for (int k = 0; k < count; k++)
                {
                    object item = Get(_acc, k, 3), dye = Get(_dyes, k, 3);
                    if (IsAir(item) || IsAir(dye) || !Allowed(player, k, false)) continue;
                    _updateDye.Invoke(player, new object[] { true, false, item, dye });
                }
            }
            catch (Exception e) { Fail("dyes", e); }
        }

        /// <summary>Inside Player.UpdateDyes, after the vanilla loop (extra vanity wins).</summary>
        public static void DyesVanity(object player)
        {
            try
            {
                int count;
                if (!Active(out count) || !IsLocal(player)) return;
                for (int k = 0; k < count; k++)
                {
                    object item = Get(_acc, k, 13), dye = Get(_dyes, k, 3);
                    if (IsAir(item) || IsAir(dye) || !Allowed(player, k, true)) continue;
                    _updateDye.Invoke(player, new object[] { false, false, item, dye });
                }
            }
            catch (Exception e) { Fail("vanity dyes", e); }
        }

        /// <summary>In Player.PlayerFrame, before UpdateVisibleAccessories (vanilla visuals override these).</summary>
        public static void VisualsFunctional(object player)
        {
            try
            {
                int count;
                if (!Active(out count) || !IsLocal(player)) return;
                foreach (int k in Functionals(player, count))
                {
                    object item = Get(_acc, k, 3);
                    if ((bool)_visuallyIncompatible.Invoke(player, new object[] { item })) continue;
                    int wing = (sbyte)_fWingSlot.GetValue(item);
                    // like vanilla, hidden wings still show while airborne
                    if (wing > 0 && (!_hidden[k] || Airborne(player))) _pWings.SetValue(player, wing);
                    if (!_hidden[k]) _visible.Invoke(player, new object[] { 3, item });
                }
            }
            catch (Exception e) { Fail("visuals", e); }
        }

        /// <summary>In Player.PlayerFrame, after UpdateVisibleAccessories.</summary>
        public static void VisualsVanity(object player)
        {
            try
            {
                int count;
                if (!Active(out count) || !IsLocal(player)) return;
                foreach (object item in Vanities(count))
                    if (!(bool)_visuallyIncompatible.Invoke(player, new object[] { item }))
                        _visible.Invoke(player, new object[] { 13, item });
            }
            catch (Exception e) { Fail("vanity visuals", e); }
        }

        // ------------------------------------------------------------------ which items count

        /// <summary>Functional extras that currently apply: accessories that pass the expert gate and are compatible with the rest.</summary>
        static IEnumerable<int> Functionals(object player, int count)
        {
            var applied = new List<object>();
            for (int k = 0; k < count; k++)
            {
                object item = Get(_acc, k, 3);
                if (IsAir(item) || !(bool)_fAccessory.GetValue(item)) continue;
                if ((bool)_fExpertOnly.GetValue(item) && !ExpertMode()) continue;
                if (!CompatibleWith(item, VanillaFunctional(player), false) || !CompatibleWith(item, applied, false)) continue;
                applied.Add(item);
                yield return k;
            }
        }

        static IEnumerable<object> Vanities(int count)
        {
            for (int k = 0; k < count; k++)
            {
                object item = Get(_acc, k, 13);
                if (!IsAir(item) && (bool)_fAccessory.GetValue(item)) yield return item;
            }
        }

        /// <summary>The game's pairwise rule: same item, two wings, or items in the same incompatibility group cannot be worn together.</summary>
        static bool CompatibleWith(object item, IEnumerable<object> others, bool vanity)
        {
            foreach (object other in others)
                if (!IsAir(other) && !(bool)_canBoth.Invoke(null, new object[] { item, other, vanity })) return false;
            return true;
        }

        static IEnumerable<object> VanillaFunctional(object player)
        {
            var armor = (Array)_pArmor.GetValue(player);
            for (int i = 3; i < 10; i++) yield return armor.GetValue(i);
        }

        static IEnumerable<object> VanillaVanity(object player)
        {
            var armor = (Array)_pArmor.GetValue(player);
            for (int i = 13; i < 20; i++) yield return armor.GetValue(i);
        }

        /// <summary>
        /// May the item in slot k (functional, or vanity when isVanity) stay there? Mirrors the game's own placement rules:
        /// compatible with the other items of its kind, and the same item is never both worn and shown as vanity.
        /// </summary>
        static bool Allowed(object player, int k, bool isVanity) { return AllowedItem(player, k, isVanity, Get(_acc, k, isVanity ? 13 : 3)); }

        static bool AllowedItem(object player, int k, bool isVanity, object item)
        {
            if (IsAir(item)) return true;
            int type = (int)_fType.GetValue(item);
            var sameKind = new List<object>(); var otherKind = new List<object>();
            foreach (object o in isVanity ? VanillaVanity(player) : VanillaFunctional(player)) sameKind.Add(o);
            foreach (object o in isVanity ? VanillaFunctional(player) : VanillaVanity(player)) otherKind.Add(o);
            for (int i = 0; i < ActiveCount(); i++)
            {
                if (i != k) sameKind.Add(Get(_acc, i, isVanity ? 13 : 3));
                otherKind.Add(Get(_acc, i, isVanity ? 3 : 13));
            }
            if (!CompatibleWith(item, sameKind, isVanity)) return false;
            foreach (object o in otherKind) if (!IsAir(o) && (int)_fType.GetValue(o) == type) return false;
            return true;
        }

        // ------------------------------------------------------------------ slots

        static bool Airborne(object player)
        {
            try { object v = _pVelocity.GetValue(player); return (float)v.GetType().GetField("Y").GetValue(v) != 0f; }
            catch (Exception) { return false; }
        }

        /// <summary>The vanilla-style eye: a click flips whether this accessory's look is drawn. Not part of the core feature: if anything is missing it is simply not shown.</summary>
        static void Toggle(int k, int x, int y)
        {
            if (!_tickOk || IsAir(Get(_acc, k, 3))) return;
            try
            {
                object tex = _assetValue.GetValue((_hidden[k] ? _tickOff : _tickOn).GetValue(null), null);
                int w = (int)_texWidth.GetValue(tex, null), h = (int)_texHeight.GetValue(tex, null);
                int mx = (int)_mouseX.GetValue(null), my = (int)_mouseY.GetValue(null);
                bool free = IsAir(_mouseItem.GetValue(null));
                bool hover = free && !(bool)_ignoreMouse.GetValue(null, null) && mx >= x && mx < x + w && my >= y && my < y + h;
                if (hover)
                {
                    _fMouseInterface.SetValue(LocalPlayer(), true);
                    if ((bool)_mouseLeft.GetValue(null) && (bool)_mouseLeftRelease.GetValue(null))
                    {
                        _hidden[k] = !_hidden[k];
                        tex = _assetValue.GetValue((_hidden[k] ? _tickOff : _tickOn).GetValue(null), null);
                        try { _playSound.Invoke(null, new object[] { 12, -1, -1, 1, 1f, 0f }); } catch (Exception) { }
                    }
                }
                if (free)
                {
                    object white = _colorT.GetProperty("White", BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
                    object dim = _colorT.GetMethod("op_Multiply", BindingFlags.Static | BindingFlags.Public, null, new[] { _colorT, typeof(float) }, null)
                        .Invoke(null, new object[] { white, 0.7f });
                    var position = Activator.CreateInstance(_vec2T, new object[] { (float)x, (float)y });
                    _spriteBatchDraw.Invoke(_spriteBatch.GetValue(null), new object[] { tex, position, dim });
                    if (hover)
                    {
                        _clearHover.Invoke(null, null);
                        object text = ((Array)_langInter.GetValue(null)).GetValue(58 + (_hidden[k] ? 2 : 1));
                        _hoverName.SetValue(null, (string)_textValue.GetValue(text, null));
                    }
                }
            }
            catch (Exception e) { _tickOk = false; GameHooks.WriteLog("Extra accessory visibility toggles switched off: " + e.GetType().Name + ": " + e.Message); }
        }

        static bool OverToggle(int k, int x, int y)
        {
            if (!_tickOk || IsAir(Get(_acc, k, 3)) || !IsAir(_mouseItem.GetValue(null))) return false;
            try
            {
                object tex = _assetValue.GetValue((_hidden[k] ? _tickOff : _tickOn).GetValue(null), null);
                int w = (int)_texWidth.GetValue(tex, null), h = (int)_texHeight.GetValue(tex, null);
                int mx = (int)_mouseX.GetValue(null), my = (int)_mouseY.GetValue(null);
                return mx >= x && mx < x + w && my >= y && my < y + h;
            }
            catch (Exception) { return false; }
        }

        static void Slot(object player, Array items, int context, int slot, int x, int y, int size, int k, bool isVanity, bool skipHandle = false)
        {
            int mx = (int)_mouseX.GetValue(null), my = (int)_mouseY.GetValue(null);
            bool hover = mx >= x && mx < x + size && my >= y && my < y + size && !skipHandle;
            if (hover && !(bool)_ignoreMouse.GetValue(null, null))
            {
                _fMouseInterface.SetValue(player, true);
                _armorHide.SetValue(null, true);
                object mouse = Main("mouseItem");
                bool blocked = context != DyeContext && !IsAir(mouse) && (bool)_fAccessory.GetValue(mouse) && !AllowedItem(player, k, isVanity, mouse);
                if (blocked) _hoverName.SetValue(null, "Can't equip: it is the same as, or conflicts with, another accessory");
                else _handle.Invoke(null, new object[] { items, context, slot, true });
            }
            var position = Activator.CreateInstance(_vec2T, new object[] { (float)x, (float)y });
            var color = Activator.CreateInstance(_colorT);
            _draw.Invoke(null, new object[] { _spriteBatch.GetValue(null), items, context, slot, position, color });
        }

        static object Get(Array[] arrays, int k, int baseIndex) { return arrays[k / PerArray].GetValue(baseIndex + k % PerArray); }

        static bool IsAir(object item) { return item == null || (int)_fType.GetValue(item) <= 0; }

        static bool IsLocal(object player) { return (int)_pWho.GetValue(player) == (int)_myPlayer.GetValue(null); }

        static object LocalPlayer() { return ((Array)_players.GetValue(null)).GetValue((int)_myPlayer.GetValue(null)); }

        static object Main(string field) { return _game.GetType("Terraria.Main").GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null); }

        static int ScreenWidth() { return (int)_game.GetType("Terraria.Main").GetField("screenWidth", BindingFlags.Static | BindingFlags.Public).GetValue(null); }

        static bool ExpertMode() { return (bool)_expertGetter.Invoke(null, null); }

        static int SlotSize()
        {
            var tex = _assetValue.GetValue(_invBack.GetValue(null), null);
            return (int)((int)_texWidth.GetValue(tex, null) * (float)_scale.GetValue(null));
        }

        static int ActiveCount()
        {
            var rules = GameHooks.CurrentRules;
            return rules == null ? 0 : Math.Min(MaxExtra, Math.Max(0, rules.ExtraAccessories));
        }

        static bool Active(out int count)
        {
            count = _dead ? 0 : ActiveCount();
            if (count == 0) return false;
            if (!_init) Init();
            if (_dead) { count = 0; return false; }
            return true;
        }

        // ------------------------------------------------------------------ persistence (per character)

        static void SyncWithCharacter()
        {
            string path = CharacterPath();
            if (path == null || path == _loadedFor) return;
            _hidden = new bool[MaxExtra];
            for (int a = 0; a < _acc.Length; a++)
                for (int j = 0; j < PerArray; j++) { Clear(_acc[a].GetValue(3 + j)); Clear(_acc[a].GetValue(13 + j)); Clear(_dyes[a].GetValue(3 + j)); }
            string file = StoreFile(path);
            if (File.Exists(file))
                foreach (string line in File.ReadAllLines(file))
                {
                    string[] p = line.Split(' ');
                    int k, type, prefix;
                    if (p.Length != 4 || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out k)
                        || !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out type)
                        || !int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix)) continue;
                    if (k < 0 || k >= MaxExtra) continue;
                    if (p[0] == "h") { _hidden[k] = type == 1; continue; }
                    if (type <= 0) continue;
                    object item = p[0] == "a" ? Get(_acc, k, 3) : p[0] == "v" ? Get(_acc, k, 13) : p[0] == "d" ? Get(_dyes, k, 3) : null;
                    if (item == null) continue;
                    _setDefaults.Invoke(item, new object[] { type, null });
                    _fStack.SetValue(item, 1);
                    if (prefix > 0) _prefix.Invoke(item, new object[] { prefix });
                }
            _loadedFor = path;
            _lastSaved = Snapshot();
            GameHooks.WriteLog("Extra accessory slots loaded for " + Path.GetFileName(path) + ".");
        }

        static void SaveIfChanged()
        {
            string path = _loadedFor;
            if (path == null) return;
            string now = Snapshot();
            if (now == _lastSaved) return;
            string file = StoreFile(path);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, now);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            _lastSaved = now;
        }

        static string Snapshot()
        {
            var sb = new StringBuilder();
            for (int k = 0; k < MaxExtra; k++)
            {
                Line(sb, "a", k, Get(_acc, k, 3));
                Line(sb, "v", k, Get(_acc, k, 13));
                Line(sb, "d", k, Get(_dyes, k, 3));
                if (_hidden[k]) sb.Append("h ").Append(k).Append(" 1 0\n");
            }
            return sb.ToString();
        }

        static void Line(StringBuilder sb, string kind, int k, object item)
        {
            int type = (int)_fType.GetValue(item);
            if (type > 0) sb.Append(kind).Append(' ').Append(k).Append(' ').Append(type).Append(' ').Append((byte)_fPrefix.GetValue(item)).Append('\n');
        }

        static void Clear(object item)
        {
            _fType.SetValue(item, 0); _fStack.SetValue(item, 0); _fPrefix.SetValue(item, (byte)0);
        }

        static string CharacterPath()
        {
            object data = _activeFile.GetValue(null);
            return data == null ? null : _filePath.GetValue(data, null) as string;
        }

        static string StoreFile(string characterPath)
        {
            uint h = 2166136261;
            foreach (char c in characterPath.ToLowerInvariant()) { h ^= c; h *= 16777619; }
            string name = Path.GetFileNameWithoutExtension(characterPath);
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            return Path.Combine(Path.Combine(GameHooks.DataDir, "Accessories"), name + "_" + h.ToString("x8") + ".txt");
        }

        // ------------------------------------------------------------------ setup and failure handling

        static void Init()
        {
            try
            {
                _game = GameHooks.GameAssembly;
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type main = _game.GetType("Terraria.Main", true), player = _game.GetType("Terraria.Player", true), slotT = _game.GetType("Terraria.UI.ItemSlot", true);
                _itemT = _game.GetType("Terraria.Item", true);
                Type itemArray = _itemT.MakeArrayType();

                _draw = FindStatic(slotT, "Draw", 6, itemArray, 1);
                _handle = FindStatic(slotT, "Handle", 4, itemArray, 0);
                _canBoth = slotT.GetMethod("CanEquipBothAccessories", S);
                _vec2T = _draw.GetParameters()[4].ParameterType;
                _colorT = _draw.GetParameters()[5].ParameterType;

                _mouseX = main.GetField("mouseX", S); _mouseY = main.GetField("mouseY", S);
                _scale = main.GetField("inventoryScale", S); _spriteBatch = main.GetField("spriteBatch", S);
                _armorHide = main.GetField("armorHide", S); _myPlayer = main.GetField("myPlayer", S); _players = main.GetField("player", S);
                _hoverName = main.GetField("hoverItemName", S); _activeFile = main.GetField("ActivePlayerFileData", S);
                _expertGetter = main.GetMethod("get_expertMode", S);
                _filePath = _activeFile.FieldType.GetProperty("Path", I);
                _ignoreMouse = _game.GetType("Terraria.GameInput.PlayerInput", true).GetProperty("IgnoreMouseInterface", S);
                _invBack = _game.GetType("Terraria.GameContent.TextureAssets", true).GetField("InventoryBack", S);
                _assetValue = _invBack.FieldType.GetProperty("Value", I);
                _texWidth = _assetValue.PropertyType.GetProperty("Width", I);

                _fType = _itemT.GetField("type", I); _fStack = _itemT.GetField("stack", I); _fPrefix = _itemT.GetField("prefix", I);
                _fAccessory = _itemT.GetField("accessory", I); _fExpertOnly = _itemT.GetField("expertOnly", I); _fWingSlot = _itemT.GetField("wingSlot", I);
                _fMouseInterface = player.GetField("mouseInterface", I);
                _pArmor = player.GetField("armor", I); _pHide = player.GetField("hideVisibleAccessory", I); _pWings = player.GetField("wings", I);
                _pWho = _game.GetType("Terraria.Entity", true).GetField("whoAmI", I);
                _setDefaults = FindInstance(_itemT, "SetDefaults", 2, typeof(int));
                _prefix = _itemT.GetMethod("Prefix", I, null, new[] { typeof(int) }, null);
                _grantPrefix = player.GetMethod("GrantPrefixBenefits", I, null, new[] { _itemT }, null);
                _grantArmor = player.GetMethod("GrantArmorBenefits", I, null, new[] { _itemT }, null);
                _applyFunctional = player.GetMethod("ApplyEquipFunctional", I, null, new[] { typeof(int), _itemT }, null);
                _applyVanity = player.GetMethod("ApplyEquipVanity", I, null, new[] { typeof(int), _itemT }, null);
                _updateDye = player.GetMethod("UpdateItemDye", I, null, new[] { typeof(bool), typeof(bool), _itemT, _itemT }, null);
                _visible = player.GetMethod("UpdateVisibleAccessory", I, null, new[] { typeof(int), _itemT }, null);
                _visuallyIncompatible = player.GetMethod("ItemIsVisuallyIncompatible", I, null, new[] { _itemT }, null);

                foreach (object member in new object[] { _draw, _handle, _canBoth, _mouseX, _mouseY, _scale, _spriteBatch, _armorHide, _myPlayer, _players, _hoverName,
                    _activeFile, _expertGetter, _filePath, _ignoreMouse, _invBack, _assetValue, _texWidth, _fType, _fStack, _fPrefix, _fAccessory, _fExpertOnly,
                    _fWingSlot, _fMouseInterface, _pArmor, _pHide, _pWings, _pWho, _setDefaults, _prefix, _grantPrefix, _grantArmor, _applyFunctional,
                    _applyVanity, _updateDye, _visible, _visuallyIncompatible })
                    if (member == null) throw new InvalidOperationException("a game member this feature needs was not found (different game version?)");

                try
                {
                    Type tex = _game.GetType("Terraria.GameContent.TextureAssets", true), batch = _spriteBatch.FieldType;
                    _tickOn = tex.GetField("InventoryTickOn", S); _tickOff = tex.GetField("InventoryTickOff", S);
                    _mouseLeft = main.GetField("mouseLeft", S); _mouseLeftRelease = main.GetField("mouseLeftRelease", S); _mouseItem = main.GetField("mouseItem", S);
                    _pVelocity = player.GetField("velocity", I); _clearHover = main.GetMethod("ClearHoverItem", S);
                    _langInter = _game.GetType("Terraria.Lang", true).GetField("inter", S);
                    _textValue = _langInter.FieldType.GetElementType().GetProperty("Value", I);
                    _texHeight = _assetValue.PropertyType.GetProperty("Height", I);
                    Type sound = _game.GetType("Terraria.Audio.SoundEngine", true);
                    foreach (MethodInfo m in sound.GetMethods(S))
                        if (m.Name == "PlaySound" && m.GetParameters().Length == 6 && m.GetParameters()[0].ParameterType == typeof(int)) _playSound = m;
                    foreach (MethodInfo m in batch.GetMethods(I))
                    {
                        ParameterInfo[] q = m.GetParameters();
                        if (m.Name == "Draw" && q.Length == 3 && q[0].ParameterType == _assetValue.PropertyType && q[1].ParameterType == _vec2T && q[2].ParameterType == _colorT) _spriteBatchDraw = m;
                    }
                    _tickOk = _tickOn != null && _tickOff != null && _mouseLeft != null && _mouseLeftRelease != null && _mouseItem != null && _pVelocity != null
                              && _clearHover != null && _langInter != null && _textValue != null && _texHeight != null && _playSound != null && _spriteBatchDraw != null;
                }
                catch (Exception) { _tickOk = false; }
                if (!_tickOk) GameHooks.WriteLog("Extra accessory visibility toggles unavailable (different game version?); the slots themselves still work.");

                int arrays = (MaxExtra + PerArray - 1) / PerArray;
                _acc = new Array[arrays]; _dyes = new Array[arrays];
                for (int a = 0; a < arrays; a++) { _acc[a] = NewItems(20); _dyes[a] = NewItems(10); }
                _init = true;
            }
            catch (Exception e)
            {
                _dead = true;
                GameHooks.WriteLog("Extra accessory slots unavailable: " + e.Message);
            }
        }

        static Array NewItems(int n)
        {
            Array a = Array.CreateInstance(_itemT, n);
            for (int i = 0; i < n; i++) a.SetValue(Activator.CreateInstance(_itemT), i);
            return a;
        }

        static MethodInfo FindStatic(Type t, string name, int args, Type arrayParamType, int arrayParamIndex)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == name && p.Length == args && p[arrayParamIndex].ParameterType == arrayParamType) return m;
            }
            return null;
        }

        static MethodInfo FindInstance(Type t, string name, int args, Type firstParam)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == name && p.Length == args && p[0].ParameterType == firstParam) return m;
            }
            return null;
        }

        static void Fail(string where, Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            _failures++;
            GameHooks.WriteLog("Extra accessory slots (" + where + ") error " + _failures + ": " + e.GetType().Name + ": " + e.Message);
            if (_failures >= 8)
            {
                _dead = true;
                GameHooks.WriteLog("Extra accessory slots switched off for this session after repeated errors. Your accessories and characters are not affected.");
            }
        }
    }
}
