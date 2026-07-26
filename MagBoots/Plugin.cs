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

    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> ConfigEnabled = null!;
        internal static ConfigEntry<KeyboardShortcut> ConfigToggleKey = null!;
        internal static ConfigEntry<InputControlType> ConfigToggleButton = null!;
        internal static ConfigEntry<bool> ConfigDebugPrint = null!;

        internal static ConfigEntry<float> ConfigMaxAttachDistance = null!;
        internal static ConfigEntry<float> ConfigMinFaceArea = null!;
        internal static ConfigEntry<float> ConfigMaxNormalAngle = null!;
        internal static ConfigEntry<float> ConfigStandoffDistance = null!;
        internal static ConfigEntry<float> ConfigSnapDuration = null!;
        internal static ConfigEntry<float> ConfigAheadCastDistance = null!;
        internal static ConfigEntry<float> ConfigMoveSpeed = null!;
        internal static ConfigEntry<float> ConfigSpring = null!;
        internal static ConfigEntry<float> ConfigDamper = null!;
        internal static ConfigEntry<float> ConfigMaxLookDownAngle = null!;
        internal static ConfigEntry<float> ConfigBatteryCapacityMinutes = null!;
        internal static ConfigEntry<BatteryDisplayMode> ConfigBatteryDisplayMode = null!;
        internal static ConfigEntry<string> ConfigHudOffset = null!;

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

        private void Awake()
        {
            Log = Logger;

            ConfigEnabled = Config.Bind("General", "Enabled", true,
                "Enable or disable the plugin (requires restart).");

            ConfigToggleKey = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.M),
                "Keyboard shortcut to attach/detach mag boots.");

            ConfigToggleButton = Config.Bind("General", "ControllerToggleButton", InputControlType.DPadDown,
                "Controller button to attach/detach mag boots.");

            ConfigDebugPrint = Config.Bind("General", "DebugPrint", false,
                "Log verbose debug info.");

            ConfigMaxAttachDistance = Config.Bind("Tuning", "MaxAttachDistance", 5f,
                "Max downward raycast distance (meters) when attempting to attach.");

            ConfigMinFaceArea = Config.Bind("Tuning", "MinFaceArea", 1f,
                "Minimum estimated face area (square meters) required to attach.");

            ConfigMaxNormalAngle = Config.Bind("Tuning", "MaxNormalAngle", 50f,
                "Maximum angle (degrees) between the surface normal and the player's down vector to allow attaching.");

            ConfigStandoffDistance = Config.Bind("Tuning", "StandoffDistance", 1.5f,
                "Distance (meters) the player is held above the attached surface.");

            ConfigSnapDuration = Config.Bind("Tuning", "SnapDuration", 1f,
                "Duration (seconds) of the initial snap-to-surface tween.");

            ConfigAheadCastDistance = Config.Bind("Tuning", "AheadCastDistance", 0.3f,
                "Distance (meters) ahead of the player to cast the next downward raycast while walking.");

            ConfigMoveSpeed = Config.Bind("Tuning", "MoveSpeed", 4f,
                "Tangential movement speed (meters/second) while attached.");

            ConfigSpring = Config.Bind("Tuning", "Spring", 200f,
                "Spring constant holding the player at the standoff distance while attached.");

            ConfigDamper = Config.Bind("Tuning", "Damper", 30f,
                "Damper constant for the standoff spring, to prevent oscillation.");

            ConfigMaxLookDownAngle = Config.Bind("Tuning", "MaxLookDownAngle", 45f,
                "Maximum angle (degrees) the player can pitch their view down toward the attached surface before it's clamped.");

            ConfigBatteryCapacityMinutes = Config.Bind("Tuning", "BatteryCapacityMinutes", 5f,
                "Minutes of battery available while attached before mag boots force-detach. Resets to full at the start of each shift. Set to 0 to disable the battery (unlimited).");

            ConfigBatteryDisplayMode = Config.Bind("HUD", "BatteryDisplayMode", BatteryDisplayMode.Gauge,
                "How to display remaining battery in the HUD hint: Gauge (segmented bar), Percentage, Timer (MM:SS), or Off (hidden).");

            ConfigHudOffset = Config.Bind("HUD", "Offset", "(-80, 0)",
                "Offset of the MagBoots HUD hint as a percentage of screen size, from its default bottom-right position. " +
                "Format: (x, y). e.g. (-15, 10) moves 15% left and 10% up. Negative y = down.");

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
            if (_controller == null || !IsInFlightGameplay || !ShouldToggle())
                return;

            _controller.OnTogglePressed();
        }

        private void FixedUpdate()
        {
            if (_controller == null || !IsInFlightGameplay)
                return;

            _controller.FixedUpdate();
        }

        private void OnGUI()
        {
            if (_controller == null || !IsInFlightGameplay)
                return;

            string keyLabel = IsControllerActive
                ? GetControllerButtonLabel(ConfigToggleButton.Value)
                : ConfigToggleKey.Value.ToString();

            Vector2 percent = ParseHudOffset();
            var offsetPixels = new Vector2(percent.x / 100f * Screen.width, -percent.y / 100f * Screen.height);

            bool showBattery = ConfigBatteryCapacityMinutes.Value > 0f && ConfigBatteryDisplayMode.Value != BatteryDisplayMode.Off;
            MagBootsHud.Draw(_controller.State, keyLabel, offsetPixels, showBattery,
                ConfigBatteryDisplayMode.Value, _controller.BatteryFraction, _controller.BatteryMinutesRemaining);
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
