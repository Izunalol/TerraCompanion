using System;
using System.Reflection;

namespace TerraCompanion.Hooks
{
    /// <summary>
    /// Input-latency and frame-pacing options (ideas from the tModLoader mods HighFPSSupport and NativeCursor, reimplemented here):
    ///   responsiveMouse  with Frame Skip Off the game draws at your refresh rate but only reads the mouse 60 times a second;
    ///                    this re-reads the mouse position on every drawn frame, so the cursor and hover follow the mouse.
    ///   nativeCursor     shows the Windows cursor instead of drawing one, so it moves with zero frame delay.
    ///   vsync / fpsCap   vanilla forces VSync on with Frame Skip Off; these let you turn it off and/or cap the frame rate.
    /// The game still simulates at 60 ticks per second in every case. Everything here only applies with Frame Skip Off.
    /// Each hook swallows its own exceptions and switches itself off after repeated errors.
    /// </summary>
    static class Performance
    {
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static bool _init, _mouseDead, _cursorDead, _timingDead;
        static int _mouseFailures, _cursorFailures, _timingFailures;

        // mouse
        static FieldInfo _mouseInfo, _pMouseX, _pMouseY, _rawScale, _gameMenu, _frameSkip, _inputMode;
        static MethodInfo _getState, _updateMain, _cacheZoom, _allowInput, _usingGamepad;
        static PropertyInfo _msX, _msY, _msScroll, _msLeft, _msMiddle, _msRight, _msX1, _msX2;
        static ConstructorInfo _msCtor;
        static FieldInfo _vecX, _vecY;

        // cursor
        static FieldInfo _instance, _reHide;

        // timing
        static FieldInfo _graphics;
        static PropertyInfo _isActive, _fixedStep, _targetElapsed, _vsyncProp, _device, _presentParams, _interval;
        static MethodInfo _applyChanges;
        static bool _vsyncTouched;
        static DateTime _lastApply = DateTime.MinValue;

        static RuleSet Rules { get { return GameHooks.CurrentRules; } }

        // ------------------------------------------------------------------ responsive mouse

        /// <summary>Main.DoUpdate, on frames that are drawn but not simulated (the early return of a partial tick).</summary>
        public static void PartialFrame()
        {
            if (_mouseDead) return;
            try
            {
                var rules = Rules;
                if (rules == null || !rules.ResponsiveMouse) return;
                if (!Init()) { _mouseDead = true; return; }
                if (Convert.ToInt32(_frameSkip.GetValue(null)) != 0) return;
                if (!(bool)_allowInput.Invoke(null, null) || (bool)_usingGamepad.Invoke(null, null)) return;
                // in menus only while the mouse is the active input (same as the tick code)
                if ((bool)_gameMenu.GetValue(null) && Convert.ToInt32(_inputMode.GetValue(null)) != 2) return;

                object now = _getState.Invoke(null, null);
                object old = _mouseInfo.GetValue(null);
                int x = (int)_msX.GetValue(now, null), y = (int)_msY.GetValue(now, null);
                if (x == (int)_msX.GetValue(old, null) && y == (int)_msY.GetValue(old, null)) return;
                // position only: buttons and wheel stay as the last tick read them, so clicks are still handled once per tick
                object merged = _msCtor.Invoke(new object[] { x, y, _msScroll.GetValue(old, null), _msLeft.GetValue(old, null),
                    _msMiddle.GetValue(old, null), _msRight.GetValue(old, null), _msX1.GetValue(old, null), _msX2.GetValue(old, null) });
                _mouseInfo.SetValue(null, merged);
                object scale = _rawScale.GetValue(null);
                _pMouseX.SetValue(null, (int)(x * (float)_vecX.GetValue(scale)));
                _pMouseY.SetValue(null, (int)(y * (float)_vecY.GetValue(scale)));
                _updateMain.Invoke(null, null);
                _cacheZoom.Invoke(null, null);
            }
            catch (Exception e) { Fail(ref _mouseFailures, ref _mouseDead, "responsive mouse", e); }
        }

        // ------------------------------------------------------------------ native cursor

        static bool NativeCursorWanted()
        {
            if (_cursorDead) return false;
            var rules = Rules;
            if (rules == null || !rules.NativeCursor) return false;
            if (!Init()) { _cursorDead = true; return false; }
            return !(bool)_usingGamepad.Invoke(null, null); // a controller still needs the drawn cursor
        }

        /// <summary>Start of FocusHelper.UpdateFocus: stop the game from re-hiding the system cursor.</summary>
        public static void BeforeFocus()
        {
            try { if (NativeCursorWanted()) _reHide.SetValue(_instance.GetValue(null), false); }
            catch (Exception e) { Fail(ref _cursorFailures, ref _cursorDead, "native cursor", e); }
        }

        /// <summary>Every "IsMouseVisible = x" in FocusHelper.UpdateFocus goes through here.</summary>
        public static bool FilterMouseVisible(bool vanilla)
        {
            try { return vanilla || NativeCursorWanted(); }
            catch (Exception e) { Fail(ref _cursorFailures, ref _cursorDead, "native cursor", e); return vanilla; }
        }

        /// <summary>Start of DrawCursor / DrawThickCursor / DrawInterface_36_Cursor: true = skip drawing the game's cursor.</summary>
        public static bool HideGameCursor()
        {
            try { return NativeCursorWanted(); }
            catch (Exception e) { Fail(ref _cursorFailures, ref _cursorDead, "native cursor", e); return false; }
        }

        // ------------------------------------------------------------------ vsync / fps cap

        /// <summary>Main.DoUpdate, right after the game sets IsFixedTimeStep and VSync for the frame-skip mode.</summary>
        public static void FrameTiming(object main)
        {
            if (_timingDead) return;
            try
            {
                var rules = Rules;
                if (rules == null || (rules.VSync && rules.FpsCap <= 0 && !_vsyncTouched)) return;
                if (!Init()) { _timingDead = true; return; }
                if (Convert.ToInt32(_frameSkip.GetValue(null)) != 0) return; // only for Frame Skip Off

                object graphics = _graphics.GetValue(null);
                _vsyncProp.SetValue(graphics, rules.VSync, null);
                ApplyVSyncIfNeeded(graphics, rules.VSync);

                if (rules.FpsCap > 0 && (bool)_isActive.GetValue(main, null))
                {
                    int cap = Math.Max(60, Math.Min(1000, rules.FpsCap));
                    _fixedStep.SetValue(main, true, null);
                    _targetElapsed.SetValue(main, TimeSpan.FromTicks(TimeSpan.TicksPerSecond / cap), null);
                }
            }
            catch (Exception e) { Fail(ref _timingFailures, ref _timingDead, "frame timing", e); }
        }

        /// <summary>The VSync property only takes effect after ApplyChanges; do that only when the device disagrees, and not more than every 2 s.</summary>
        static void ApplyVSyncIfNeeded(object graphics, bool vsync)
        {
            object device = _device.GetValue(graphics, null);
            if (device == null) return;
            object pp = _presentParams.GetValue(device, null);
            string interval = _interval.GetValue(pp, null).ToString(); // PresentInterval: Default, One, Two, Immediate
            bool deviceVsync = interval != "Immediate";
            if (deviceVsync == vsync) return;
            if ((DateTime.UtcNow - _lastApply).TotalSeconds < 2) return;
            _lastApply = DateTime.UtcNow;
            _vsyncTouched = !vsync;
            _applyChanges.Invoke(graphics, null);
            GameHooks.WriteLog("VSync " + (vsync ? "on" : "off") + ".");
        }

        // ------------------------------------------------------------------ setup

        static bool Init()
        {
            if (_init) return true;
            Assembly game = GameHooks.GameAssembly;
            Type main = game.GetType("Terraria.Main", true), input = game.GetType("Terraria.GameInput.PlayerInput", true), focus = game.GetType("Terraria.FocusHelper", true);

            _mouseInfo = input.GetField("MouseInfo", S); _pMouseX = input.GetField("MouseX", S); _pMouseY = input.GetField("MouseY", S);
            _rawScale = input.GetField("RawMouseScale", S); _inputMode = input.GetField("_inputMode", S);
            _updateMain = input.GetMethod("UpdateMainMouse", S, null, Type.EmptyTypes, null);
            _cacheZoom = input.GetMethod("CacheMousePositionForZoom", S, null, Type.EmptyTypes, null);
            _usingGamepad = input.GetMethod("get_UsingGamepad", S);
            _allowInput = focus.GetMethod("get_AllowInputProcessing", S);
            _gameMenu = main.GetField("gameMenu", S); _frameSkip = main.GetField("FrameSkipMode", S);
            _instance = main.GetField("instance", S); _reHide = main.GetField("ReHideCursor", I);
            _graphics = main.GetField("graphics", S);

            Type msT = _mouseInfo.FieldType;                                    // XNA MouseState
            Type mouseT = msT.Assembly.GetType("Microsoft.Xna.Framework.Input.Mouse", true);
            _getState = mouseT.GetMethod("GetState", S, null, Type.EmptyTypes, null);
            _msX = msT.GetProperty("X"); _msY = msT.GetProperty("Y"); _msScroll = msT.GetProperty("ScrollWheelValue");
            _msLeft = msT.GetProperty("LeftButton"); _msMiddle = msT.GetProperty("MiddleButton"); _msRight = msT.GetProperty("RightButton");
            _msX1 = msT.GetProperty("XButton1"); _msX2 = msT.GetProperty("XButton2");
            Type button = _msLeft.PropertyType;
            _msCtor = msT.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), button, button, button, button, button });
            _vecX = _rawScale.FieldType.GetField("X"); _vecY = _rawScale.FieldType.GetField("Y");

            Type gameT = main.BaseType; // Microsoft.Xna.Framework.Game
            _isActive = gameT.GetProperty("IsActive"); _fixedStep = gameT.GetProperty("IsFixedTimeStep"); _targetElapsed = gameT.GetProperty("TargetElapsedTime");
            Type gdm = _graphics.FieldType;
            _vsyncProp = gdm.GetProperty("SynchronizeWithVerticalRetrace"); _device = gdm.GetProperty("GraphicsDevice");
            _applyChanges = gdm.GetMethod("ApplyChanges", Type.EmptyTypes);
            _presentParams = _device.PropertyType.GetProperty("PresentationParameters");
            _interval = _presentParams.PropertyType.GetProperty("PresentationInterval");

            foreach (object m in new object[] { _mouseInfo, _pMouseX, _pMouseY, _rawScale, _inputMode, _updateMain, _cacheZoom, _usingGamepad, _allowInput,
                _gameMenu, _frameSkip, _instance, _reHide, _graphics, _getState, _msX, _msY, _msScroll, _msLeft, _msMiddle, _msRight, _msX1, _msX2, _msCtor,
                _vecX, _vecY, _isActive, _fixedStep, _targetElapsed, _vsyncProp, _device, _applyChanges, _presentParams, _interval })
                if (m == null)
                {
                    GameHooks.WriteLog("Performance options unavailable: a game member they need was not found (different game version?).");
                    return false;
                }
            _init = true;
            return true;
        }

        static void Fail(ref int failures, ref bool dead, string what, Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            failures++;
            GameHooks.WriteLog("Performance (" + what + ") error " + failures + ": " + e.GetType().Name + ": " + e.Message);
            if (failures >= 8)
            {
                dead = true;
                GameHooks.WriteLog("Performance (" + what + ") switched off for this session after repeated errors.");
            }
        }
    }
}
