using System;
using System.Globalization;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace MagBoots
{
    public enum BatteryDisplayMode
    {
        Gauge,
        Percentage,
        Timer,
        Off,
    }

    public enum RunActivationMode
    {
        Toggle,
        Hold,
    }

    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> ConfigEnabled = null!;
        internal static ConfigEntry<KeyboardShortcut> ConfigToggleKey = null!;
        internal static ConfigEntry<InputControlType> ConfigToggleButton = null!;
        internal static ConfigEntry<bool> ConfigDebugPrint = null!;

        internal static ConfigEntry<KeyboardShortcut> ConfigRunToggleKey = null!;
        internal static ConfigEntry<string> ConfigRunToggleButton = null!;
        internal static ConfigEntry<RunActivationMode> ConfigRunActivationMode = null!;

        internal static ConfigEntry<float> ConfigStepDownHeight = null!;
        internal static ConfigEntry<float> ConfigStepSignificantHeight = null!;
        internal static ConfigEntry<float> ConfigStepLateralSettled = null!;
        internal static ConfigEntry<float> ConfigStepTimeout = null!;
        internal static ConfigEntry<float> ConfigStepUpHeight = null!;
        internal static ConfigEntry<float> ConfigStepUpFwdHeight = null!;
        internal static ConfigEntry<float> ConfigFwdSweepAngle = null!;
        internal static ConfigEntry<float> ConfigMinFaceArea = null!;
        internal static ConfigEntry<float> ConfigMaxNormalAngle = null!;
        internal static ConfigEntry<float> ConfigMaxNormalFwdAngle = null!;
        internal static ConfigEntry<float> ConfigPlayerHeight = null!;
        internal static ConfigEntry<float> ConfigSnapDuration = null!;
        internal static ConfigEntry<float> ConfigAheadCastDistance = null!;
        internal static ConfigEntry<float> ConfigMoveSpeed = null!;
        internal static ConfigEntry<float> ConfigRunSpeed = null!;
        internal static ConfigEntry<float> ConfigCornerSmoothingSpeed = null!;
        internal static ConfigEntry<float> ConfigReorientSettledAngle = null!;
        internal static ConfigEntry<float> ConfigSpring = null!;
        internal static ConfigEntry<float> ConfigDamper = null!;
        internal static ConfigEntry<float> ConfigBreakawayVelocity = null!;
        internal static ConfigEntry<float> ConfigMaxLookDownAngle = null!;
        internal static ConfigEntry<float> ConfigBatteryCapacityMinutes = null!;
        internal static ConfigEntry<float> ConfigIdlePowerMultiplier = null!;
        internal static ConfigEntry<float> ConfigLockingPowerMultiplier = null!;
        internal static ConfigEntry<BatteryDisplayMode> ConfigBatteryDisplayMode = null!;
        internal static ConfigEntry<string> ConfigHudOffset = null!;
        internal static ConfigEntry<float> ConfigHudScale = null!;

        private MagBootsController? _controller;

        internal static Vector2 ParseHudOffset()
        {
            try
            {
                var s = ConfigHudOffset.Value.Trim().Trim('(', ')');
                var parts = s.Split(',');
                if (parts.Length != 2) throw new FormatException();
                float x = float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                float y = float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                return new Vector2(x, y);
            }
            catch
            {
                Log.LogWarning($"MagBoots: could not parse HudOffset '{ConfigHudOffset.Value}' — expected format: (x, y). Using (0, 0).");
                return Vector2.zero;
            }
        }

        private static bool IsControllerActive =>
            LynxControls.Instance != null &&
            LynxControls.Instance.LastInputType == BindingSourceType.DeviceBindingSource;

        private static bool ShouldToggle()
        {
            if (IsControllerActive)
                return InputManager.ActiveDevice?[ConfigToggleButton.Value].WasPressed ?? false;
            return ConfigToggleKey.Value.IsDown();
        }

        // RunControllerToggleButton is bound as a plain string (parsed here) rather than a second
        // ConfigEntry<InputControlType> - BepInEx auto-generates a full "Acceptable values" enum dump
        // for every enum-typed entry, and ControllerToggleButton above already carries that same dump
        // once; a second InputControlType entry would just repeat the identical multi-hundred-character
        // list a second time in the .cfg file for no benefit.
        private static InputControlType? ParseRunControllerButton()
        {
            if (Enum.TryParse(ConfigRunToggleButton.Value.Trim(), ignoreCase: true, out InputControlType parsed))
                return parsed;

            Log.LogWarning($"MagBoots: could not parse RunControllerToggleButton '{ConfigRunToggleButton.Value}' — expected an InputControlType name (e.g. LeftStickButton). Run button disabled.");
            return null;
        }

        // Run input is read as both "was pressed this frame" (for Toggle mode) and "is currently held"
        // (for Hold mode) - MagBootsController decides which one it cares about based on RunActivationMode.
        private static bool WasRunPressed()
        {
            if (IsControllerActive)
            {
                InputControlType? button = ParseRunControllerButton();
                return button.HasValue && (InputManager.ActiveDevice?[button.Value].WasPressed ?? false);
            }
            return ConfigRunToggleKey.Value.IsDown();
        }

        private static bool IsRunHeld()
        {
            if (IsControllerActive)
            {
                InputControlType? button = ParseRunControllerButton();
                return button.HasValue && (InputManager.ActiveDevice?[button.Value].IsPressed ?? false);
            }
            return ConfigRunToggleKey.Value.IsPressed();
        }

        private void Awake()
        {
            Log = Logger;

            ConfigEnabled = Config.Bind("1 - General", "Enabled", true,
                "Turns the mod on or off (requires a restart).");

            ConfigToggleKey = Config.Bind("1 - General", "ToggleKey", new KeyboardShortcut(KeyCode.M),
                "Keyboard key to attach/detach mag boots.");

            ConfigToggleButton = Config.Bind("1 - General", "ControllerToggleButton", InputControlType.DPadDown,
                "Controller button to attach/detach mag boots.");

            ConfigDebugPrint = Config.Bind("1 - General", "DebugPrint", false,
                "Prints extra troubleshooting info to the log. Leave off unless asked to turn it on.");

            ConfigRunToggleKey = Config.Bind("1 - General", "RunToggleKey", new KeyboardShortcut(KeyCode.LeftShift),
                "Keyboard key to run while attached.");

            // Bound as a string rather than a second ConfigEntry<InputControlType> - see ParseRunControllerButton
            // for why (BepInEx would otherwise print the same huge enum value list a second time).
            ConfigRunToggleButton = Config.Bind("1 - General", "RunControllerToggleButton", InputControlType.LeftStickButton.ToString(),
                "Controller button to run while attached. Use the same names as ControllerToggleButton above (e.g. LeftStickButton).");

            ConfigRunActivationMode = Config.Bind("1 - General", "RunActivationMode", RunActivationMode.Toggle,
                "How the run key/button works: Toggle means press once to start running and press again to stop; Hold means you only run while it's held down.");

            ConfigStepDownHeight = Config.Bind("2 - Attach", "StepDownHeight", 1.5f,
                "How far below your feet (in meters) mag boots will look for something to attach to.");

            ConfigStepSignificantHeight = Config.Bind("2 - Attach", "StepSignificantHeight", 0.15f,
                "How big a height change (in meters) counts as a real step up or down versus just uneven flat ground. Real steps wait for StepLateralSettled before adjusting your height, which keeps steep stairs from building up speed and knocking you loose.");

            ConfigStepLateralSettled = Config.Bind("2 - Attach", "StepLateralSettled", 0.3f,
                "How closely you need to catch up to a detected step before mag boots adjusts your height to match it, as a fraction of your stride length (AheadCastDistance). 0.3 means within 30% of a stride. Moving forward isn't held up, only the up/down adjustment - this keeps steep stairs from feeling like a fast slide. Lower is stricter (closer catch-up needed); higher is looser.");

            ConfigStepTimeout = Config.Bind("2 - Attach", "StepTimeout", 0.5f,
                "Maximum time (in seconds) mag boots will wait for you to catch up to a detected step before adjusting your height anyway. Prevents ever getting stuck waiting, even if StepLateralSettled is never reached (e.g. while continuously running).");

            ConfigStepUpHeight = Config.Bind("2 - Attach", "StepUpHeight", 0.25f,
                "How far above your feet (in meters) mag boots will look for something to step up onto while already attached, for footholds off to the side or behind you. Lower makes it harder to accidentally step up onto something you didn't mean to.");

            ConfigStepUpFwdHeight = Config.Bind("2 - Attach", "StepUpFwdHeight", 0.95f,
                "A taller version of StepUpHeight that only applies to footholds within FwdSweepAngle in front of you - lets you step up onto taller ledges and obstacles you're actually walking toward, while things off to the side or behind you still use the shorter StepUpHeight.");

            ConfigMinFaceArea = Config.Bind("2 - Attach", "MinFaceArea", 3f,
                "How big a surface needs to be (in square meters) before you can attach to it. Keeps you from sticking to tiny brackets and pipes.");

            ConfigMaxNormalAngle = Config.Bind("2 - Attach", "MaxNormalAngle", 20f,
                "How tilted a surface can be (in degrees) and still count as \"flat enough\" to attach to.");

            ConfigMaxNormalFwdAngle = Config.Bind("2 - Attach", "MaxNormalFwdAngle", 50f,
                "A looser version of MaxNormalAngle that only applies to footholds within FwdSweepAngle in front of you - lets you walk up steeper ramps and inclines you're facing, while footholds off to the side or behind you still use the stricter MaxNormalAngle.");

            ConfigFwdSweepAngle = Config.Bind("2 - Attach", "FwdSweepAngle", 60f,
                "How wide a cone in front of you (in degrees) counts as \"forward\" for MaxNormalFwdAngle and StepUpFwdHeight. 60 means 30 degrees to either side of dead ahead.");

            ConfigPlayerHeight = Config.Bind("2 - Attach", "PlayerHeight", 1.5f,
                "How far off the surface (in meters) you float once attached.");

            ConfigSnapDuration = Config.Bind("2 - Attach", "SnapDuration", 1f,
                "How long (in seconds) the initial snap into place takes.");

            ConfigAheadCastDistance = Config.Bind("3 - Movement", "AheadCastDistance", 0.85f,
                "Your stride length while walking (in meters) - how far ahead mag boots checks for the next foothold.");

            ConfigMoveSpeed = Config.Bind("3 - Movement", "MoveSpeed", 2f,
                "Walking speed (in meters/second) while attached.");

            ConfigRunSpeed = Config.Bind("3 - Movement", "RunSpeed", 4f,
                "Running speed (in meters/second) while attached. Battery drains proportionally faster while running.");

            ConfigCornerSmoothingSpeed = Config.Bind("3 - Movement", "CornerSmoothingSpeed", 3f,
                "How quickly you lean into a sharp corner, instead of snapping right into the new angle. Lower is smoother/slower; higher is snappier.");

            ConfigReorientSettledAngle = Config.Bind("3 - Movement", "ReorientSettledAngle", 1f,
                "How closely you need to finish leaning into a new angle before taking the next step. Lower is stricter (smoother, but pauses more); higher is looser.");

            ConfigMaxLookDownAngle = Config.Bind("3 - Movement", "MaxLookDownAngle", 45f,
                "How far you can look down toward the surface before your view is stopped, so you can't tip over and stare at your own feet.");

            ConfigSpring = Config.Bind("4 - Physics", "Spring", 200f,
                "How firmly mag boots pull you back to the surface if you drift away. Higher is snappier.");

            ConfigDamper = Config.Bind("4 - Physics", "Damper", 30f,
                "Smooths out that pull-back so it doesn't bounce or overshoot. Higher is calmer.");

            ConfigBreakawayVelocity = Config.Bind("4 - Physics", "BreakawayVelocity", 10f,
                "How hard you need to be hit (in meters/second) before mag boots let go instead of holding on. Set very high to basically never let go.");

            ConfigBatteryCapacityMinutes = Config.Bind("5 - Battery", "BatteryCapacityMinutes", 5f,
                "How many minutes of attached time you get per shift before the battery runs out. Refills at the start of every shift. Set to 0 for unlimited.");

            ConfigIdlePowerMultiplier = Config.Bind("5 - Battery", "IdlePowerMultiplier", 0.1f,
                "How much battery you use while standing still and attached, compared to walking. 0.1 means standing still uses a tenth as much power as moving. 1 means standing still costs the same as moving.");

            ConfigLockingPowerMultiplier = Config.Bind("5 - Battery", "LockingPowerMultiplier", 10f,
                "How much battery the initial snap-into-place takes, compared to normal attached use. At the default SnapDuration of 1 second and a multiplier of 10, snapping into place costs as much battery as 10 seconds of normal attached time.");

            ConfigBatteryDisplayMode = Config.Bind("6 - HUD", "BatteryDisplayMode", BatteryDisplayMode.Gauge,
                "How the battery is shown on screen: Gauge (bar), Percentage, Timer (minutes:seconds), or Off (hidden).");

            ConfigHudOffset = Config.Bind("6 - HUD", "Offset", "(0, -48)",
                "Moves the on-screen hint from the center of your screen, as a percent of your screen size. " +
                "Format: (x, y). e.g. (0, -48) moves it to the bottom-center. Negative x moves it left, negative y moves it down.");

            ConfigHudScale = Config.Bind("6 - HUD", "Scale", 1f,
                "Size of the on-screen hint. 1 is the default size, 2 is twice as big, 0.5 is half as big.");

            if (!ConfigEnabled.Value)
            {
                Log.LogInfo("MagBoots is disabled via config.");
                return;
            }

            _controller = new MagBootsController();
            Main.EventSystem.AddHandler<GameStateChangedEvent>(OnGameStateChanged);

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            Log.LogInfo("MagBoots loaded. Press " + ConfigToggleKey.Value + " to attach/detach mag boots.");
        }

        private void OnDestroy()
        {
            Main.EventSystem.RemoveHandler<GameStateChangedEvent>(OnGameStateChanged);
        }

        // Same GameState transition GrabController itself uses to detect the start of a new shift.
        private void OnGameStateChanged(GameStateChangedEvent ev)
        {
            if (ev.GameState == GameSession.GameState.Gameplay && ev.PrevGameState == GameSession.GameState.LoadingComplete)
            {
                _controller?.OnShiftStart();
            }
            else if (ev.GameState != GameSession.GameState.Gameplay)
            {
                // Leaving flight gameplay (pause, Hab, loading, NIS, etc.) - force a clean detach so the
                // player isn't left mid-snap or drifting with stale spring forces applied outside flight.
                _controller?.OnLeaveGameplay();
            }
        }

        private static bool IsInFlightGameplay => GameSession.CurrentGameState == GameSession.GameState.Gameplay;

        private void Update()
        {
            if (_controller == null || !IsInFlightGameplay)
                return;

            if (ShouldToggle())
                _controller.OnTogglePressed();

            if (ConfigRunActivationMode.Value == RunActivationMode.Toggle)
            {
                if (WasRunPressed())
                    _controller.OnRunTogglePressed();
            }
            else
            {
                _controller.SetRunHeld(IsRunHeld());
            }
        }

        private void FixedUpdate()
        {
            if (!IsInFlightGameplay)
                return;

            _controller?.FixedUpdate();
        }

        private void OnGUI()
        {
            if (_controller == null || !IsInFlightGameplay)
                return;

            string keyLabel = IsControllerActive
                ? GetControllerButtonLabel(ConfigToggleButton.Value)
                : ConfigToggleKey.Value.ToString();

            string runKeyLabel = GetRunKeyLabel();

            Vector2 percent = ParseHudOffset();
            var offsetPixels = new Vector2(percent.x / 100f * Screen.width, -percent.y / 100f * Screen.height);

            bool showBattery = ConfigBatteryCapacityMinutes.Value > 0f && ConfigBatteryDisplayMode.Value != BatteryDisplayMode.Off;
            // Run can be toggled/held independently of being attached, but it has no effect until you
            // actually are attached - the chip shouldn't show "active" from a run toggle left on before
            // (or after) attaching, only while it's actually doing something.
            bool showRunActive = _controller.IsAttached && _controller.IsRunning;
            MagBootsHud.Draw(_controller.State, keyLabel, runKeyLabel, showRunActive, offsetPixels, showBattery,
                ConfigBatteryDisplayMode.Value, _controller.BatteryFraction, _controller.BatteryMinutesRemaining, ConfigHudScale.Value);
        }

        private static string GetRunKeyLabel()
        {
            if (!IsControllerActive)
                return ConfigRunToggleKey.Value.ToString();

            InputControlType? button = ParseRunControllerButton();
            return button.HasValue ? GetControllerButtonLabel(button.Value) : "?";
        }

        // Mirrors QuickCutscene's button-name switch, but keyed off the active device's DeviceStyle
        // so it reflects whichever controller is actually connected (Xbox vs PlayStation naming),
        // rather than always assuming Xbox.
        private static string GetControllerButtonLabel(InputControlType button)
        {
            bool isPlayStation = InputManager.ActiveDevice != null &&
                (InputManager.ActiveDevice.DeviceStyle == InputDeviceStyle.PlayStation3 ||
                 InputManager.ActiveDevice.DeviceStyle == InputDeviceStyle.PlayStation4 ||
                 InputManager.ActiveDevice.DeviceStyle == InputDeviceStyle.PlayStation5);

            return button switch
            {
                InputControlType.Action1 => isPlayStation ? "Cross" : "A",
                InputControlType.Action2 => isPlayStation ? "Circle" : "B",
                InputControlType.Action3 => isPlayStation ? "Square" : "X",
                InputControlType.Action4 => isPlayStation ? "Triangle" : "Y",
                InputControlType.Start => isPlayStation ? "Options" : "Start",
                var other => other.ToString(),
            };
        }
    }
}
