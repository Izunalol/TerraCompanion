using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace TerraCompanion.Hooks
{
    /// <summary>
    /// Extra pet / light pet equipment slots on the Equipment page.
    ///
    /// They live in OUR OWN Item[] arrays, not in Player.miscEquips: that array's length is baked into the character
    /// file format and into the multiplayer sync code, so growing it could corrupt characters or other players' slots.
    /// The slots are drawn and clicked with the game's own ItemSlot.Draw / ItemSlot.Handle (so they look and behave like
    /// the vanilla pet slots), their pets are applied as buffs every frame like vanilla slot 0/1, and their contents are
    /// stored per character in a small text file next to rules.txt.
    ///
    /// Entry points (called from IL the patcher injects):
    ///   Draw   - at the end of the Equipment-page slot loop in Main.DrawInventory
    ///   Update - right after Player.UpdatePetLight in Player.Update
    /// Both swallow every exception, and switch the feature off after repeated failures, so a problem here can never crash the game.
    /// </summary>
    static class ExtraSlots
    {
        const int MaxExtra = 4;     // extra slots per kind (so up to 5 pets and 5 light pets in total)
        const int ColumnStep = 47;  // the game spaces slot columns / rows 47 px apart
        const int PetContext = 19, LightContext = 20; // ItemSlot.Context.EquipPet / EquipLight

        static bool _init, _dead;
        static int _failures;

        static Assembly _game;
        static Type _itemT, _vec2T, _colorT;
        static MethodInfo _draw, _handle, _setDefaults, _prefix, _findBuff, _addBuff;
        static FieldInfo _mouseX, _mouseY, _scale, _spriteBatch, _armorHide, _myPlayer, _players, _vanityPet, _lightPet, _activeFile, _invBack;
        static FieldInfo _fType, _fStack, _fPrefix, _fBuff, _fMouseInterface;
        static PropertyInfo _ignoreMouse, _assetValue, _texWidth, _filePath;

        static Array _pets, _lights;
        static string _loadedFor, _lastSaved = "";

        // ------------------------------------------------------------------ entry points

        public static void Draw(int baseX, int baseY)
        {
            try
            {
                int pets, lights;
                if (!Active(out pets, out lights)) return;
                object player = LocalPlayer();
                if (player == null) return;
                SyncWithCharacter();

                int size = SlotSize();
                for (int k = 0; k < pets; k++) Slot(player, _pets, PetContext, k, baseX - (2 + k) * ColumnStep, baseY, size);
                for (int k = 0; k < lights; k++) Slot(player, _lights, LightContext, k, baseX - (2 + k) * ColumnStep, baseY + ColumnStep, size);
                SaveIfChanged();
            }
            catch (Exception e) { Fail("draw", e); }
        }

        public static void Update(object player, int index)
        {
            try
            {
                int pets, lights;
                if (!Active(out pets, out lights)) return;
                if (index != (int)_myPlayer.GetValue(null)) return;
                SyncWithCharacter();
                // same rule as vanilla's UpdatePet: a slotted pet keeps its buff on (added again if it ran out)
                Apply(player, _pets, pets);
                Apply(player, _lights, lights);
            }
            catch (Exception e) { Fail("update", e); }
        }

        // ------------------------------------------------------------------ slots

        static bool Active(out int pets, out int lights)
        {
            pets = lights = 0;
            if (_dead) return false;
            var rules = GameHooks.CurrentRules;
            if (rules == null || !rules.PetSlots) return false;
            pets = Math.Min(MaxExtra, Math.Max(0, rules.MaxPets - 1));
            lights = Math.Min(MaxExtra, Math.Max(0, rules.MaxLightPets - 1));
            if (pets == 0 && lights == 0) return false;
            if (!_init) Init();
            return !_dead;
        }

        static void Slot(object player, Array items, int context, int slot, int x, int y, int size)
        {
            int mx = (int)_mouseX.GetValue(null), my = (int)_mouseY.GetValue(null);
            bool hover = mx >= x && mx < x + size && my >= y && my < y + size;
            if (hover && !(bool)_ignoreMouse.GetValue(null, null))
            {
                _fMouseInterface.SetValue(player, true);
                _armorHide.SetValue(null, true);
                _handle.Invoke(null, new object[] { items, context, slot, true });
            }
            var position = Activator.CreateInstance(_vec2T, new object[] { (float)x, (float)y });
            var color = Activator.CreateInstance(_colorT);
            _draw.Invoke(null, new object[] { _spriteBatch.GetValue(null), items, context, slot, position, color });
        }

        static void Apply(object player, Array items, int count)
        {
            var pet = (bool[])_vanityPet.GetValue(null);
            var light = (bool[])_lightPet.GetValue(null);
            for (int k = 0; k < count; k++)
            {
                object item = items.GetValue(k);
                int buff = (int)_fBuff.GetValue(item);
                if (buff < 1 || (int)_fStack.GetValue(item) < 1) continue;
                if (buff >= pet.Length || (!pet[buff] && !light[buff])) continue;
                if ((int)_findBuff.Invoke(player, new object[] { buff }) == -1)
                    _addBuff.Invoke(player, new object[] { buff, 3600, false });
            }
        }

        static int SlotSize()
        {
            // same size the vanilla slots use: inventory background texture width x inventory scale
            var tex = _assetValue.GetValue(_invBack.GetValue(null), null);
            int width = (int)_texWidth.GetValue(tex, null);
            return (int)(width * (float)_scale.GetValue(null));
        }

        static object LocalPlayer()
        {
            var players = (Array)_players.GetValue(null);
            return players.GetValue((int)_myPlayer.GetValue(null));
        }

        // ------------------------------------------------------------------ persistence (per character)

        /// <summary>Loads this character's slot contents when the active character changes.</summary>
        static void SyncWithCharacter()
        {
            string path = CharacterPath();
            if (path == null || path == _loadedFor) return;
            for (int k = 0; k < MaxExtra; k++) { Clear(_pets, k); Clear(_lights, k); }
            string file = StoreFile(path);
            if (File.Exists(file))
                foreach (string line in File.ReadAllLines(file))
                {
                    string[] p = line.Split(' ');
                    int slot, type, prefix;
                    if (p.Length != 4 || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)
                        || !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out type)
                        || !int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix)) continue;
                    Array target = p[0] == "p" ? _pets : p[0] == "l" ? _lights : null;
                    if (target == null || slot < 0 || slot >= MaxExtra || type <= 0) continue;
                    object item = target.GetValue(slot);
                    _setDefaults.Invoke(item, new object[] { type, null });
                    _fStack.SetValue(item, 1);
                    if (prefix > 0) _prefix.Invoke(item, new object[] { prefix });
                }
            _loadedFor = path;
            _lastSaved = Snapshot();
            GameHooks.WriteLog("Extra pet slots loaded for " + Path.GetFileName(path) + ".");
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
            Append(sb, "p", _pets);
            Append(sb, "l", _lights);
            return sb.ToString();
        }

        static void Append(StringBuilder sb, string kind, Array items)
        {
            for (int k = 0; k < MaxExtra; k++)
            {
                object item = items.GetValue(k);
                int type = (int)_fType.GetValue(item);
                if (type > 0)
                    sb.Append(kind).Append(' ').Append(k).Append(' ').Append(type).Append(' ').Append((byte)_fPrefix.GetValue(item)).Append('\n');
            }
        }

        static void Clear(Array items, int k)
        {
            _fType.SetValue(items.GetValue(k), 0);
            _fStack.SetValue(items.GetValue(k), 0);
            _fPrefix.SetValue(items.GetValue(k), (byte)0);
        }

        static string CharacterPath()
        {
            object data = _activeFile.GetValue(null);
            return data == null ? null : _filePath.GetValue(data, null) as string;
        }

        /// <summary>One small file per character: &lt;saves&gt;/TerraCompanion/PetSlots/&lt;name&gt;_&lt;hash of full path&gt;.txt</summary>
        static string StoreFile(string characterPath)
        {
            uint h = 2166136261;
            foreach (char c in characterPath.ToLowerInvariant()) { h ^= c; h *= 16777619; }
            string name = Path.GetFileNameWithoutExtension(characterPath);
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            return Path.Combine(Path.Combine(GameHooks.DataDir, "PetSlots"), name + "_" + h.ToString("x8") + ".txt");
        }

        // ------------------------------------------------------------------ setup and failure handling

        static void Init()
        {
            try
            {
                _game = GameHooks.GameAssembly;
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type main = _game.GetType("Terraria.Main", true), player = _game.GetType("Terraria.Player", true);
                Type slotT = _game.GetType("Terraria.UI.ItemSlot", true);
                _itemT = _game.GetType("Terraria.Item", true);
                Type itemArray = _itemT.MakeArrayType();

                _draw = FindStatic(slotT, "Draw", 6, itemArray, 1);
                _handle = FindStatic(slotT, "Handle", 4, itemArray, 0);
                _vec2T = _draw.GetParameters()[4].ParameterType;
                _colorT = _draw.GetParameters()[5].ParameterType;

                _mouseX = main.GetField("mouseX", S); _mouseY = main.GetField("mouseY", S);
                _scale = main.GetField("inventoryScale", S); _spriteBatch = main.GetField("spriteBatch", S);
                _armorHide = main.GetField("armorHide", S); _myPlayer = main.GetField("myPlayer", S); _players = main.GetField("player", S);
                _vanityPet = main.GetField("vanityPet", S); _lightPet = main.GetField("lightPet", S);
                _activeFile = main.GetField("ActivePlayerFileData", S);
                _filePath = _activeFile.FieldType.GetProperty("Path", I);
                _ignoreMouse = _game.GetType("Terraria.GameInput.PlayerInput", true).GetProperty("IgnoreMouseInterface", S);
                _invBack = _game.GetType("Terraria.GameContent.TextureAssets", true).GetField("InventoryBack", S);
                _assetValue = _invBack.FieldType.GetProperty("Value", I);
                _texWidth = _assetValue.PropertyType.GetProperty("Width", I);

                _fType = _itemT.GetField("type", I); _fStack = _itemT.GetField("stack", I); _fPrefix = _itemT.GetField("prefix", I);
                _fBuff = _itemT.GetField("buffType", I); _fMouseInterface = player.GetField("mouseInterface", I);
                _setDefaults = FindInstance(_itemT, "SetDefaults", 2, typeof(int));
                _prefix = _itemT.GetMethod("Prefix", I, null, new[] { typeof(int) }, null);
                _findBuff = player.GetMethod("FindBuffIndex", I, null, new[] { typeof(int) }, null);
                _addBuff = player.GetMethod("AddBuff", I, null, new[] { typeof(int), typeof(int), typeof(bool) }, null);

                foreach (object member in new object[] { _draw, _handle, _mouseX, _mouseY, _scale, _spriteBatch, _armorHide, _myPlayer, _players, _vanityPet,
                    _lightPet, _activeFile, _filePath, _ignoreMouse, _invBack, _assetValue, _texWidth, _fType, _fStack, _fPrefix, _fBuff, _fMouseInterface,
                    _setDefaults, _prefix, _findBuff, _addBuff })
                    if (member == null) throw new InvalidOperationException("a game member this feature needs was not found (different game version?)");

                _pets = NewItems(); _lights = NewItems();
                _init = true;
            }
            catch (Exception e)
            {
                _dead = true;
                GameHooks.WriteLog("Extra pet slots unavailable: " + e.Message);
            }
        }

        static Array NewItems()
        {
            Array a = Array.CreateInstance(_itemT, MaxExtra);
            for (int k = 0; k < MaxExtra; k++) a.SetValue(Activator.CreateInstance(_itemT), k);
            return a;
        }

        static MethodInfo FindStatic(Type t, string name, int args, Type firstParamType, int arrayParamIndex)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == name && p.Length == args && p[arrayParamIndex].ParameterType == firstParamType) return m;
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
            GameHooks.WriteLog("Extra pet slots (" + where + ") error " + _failures + ": " + e.GetType().Name + ": " + e.Message);
            if (_failures >= 5)
            {
                _dead = true;
                GameHooks.WriteLog("Extra pet slots switched off for this session after repeated errors. Your pets and characters are not affected.");
            }
        }
    }
}
