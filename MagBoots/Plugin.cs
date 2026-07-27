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
        internal static ConfigEntry<float> ConfigCornerSmoothingSpeed = null!;
        internal static ConfigEntry<float> ConfigReorientSettledAngle = null!;
        internal static ConfigEntry<float> ConfigSpring = null!;
        internal static ConfigEntry<float> ConfigDamper = null!;
        internal static ConfigEntry<float> ConfigBreakawayVelocity = null!;
        internal static ConfigEntry<float> ConfigMaxLookDownAngle = null!;
        internal static ConfigEntry<float> ConfigBatteryCapacityMinutes = null!;
        internal static ConfigEntry<BatteryDisplayMode> ConfigBatteryDisplayMode = null!;
        internal static ConfigEntry<string> ConfigHudOffset = null!;
        internal static ConfigEntry<float> ConfigHudScale = null!;

        internal static ConfigEntry<bool> ConfigRecoilEnabled = null!;
        internal static ConfigEntry<float> ConfigSawCutterRecoil = null!;
        internal static ConfigEntry<float> ConfigScalpelCutterRecoil = null!;
        internal static ConfigEntry<float> ConfigGrappleThrowRecoilMultiplier = null!;
        internal static ConfigEntry<float> ConfigGrappleReflectionRecoil = null!;
        internal static ConfigEntry<float> ConfigGrappleReflectionDistance = null!;
        internal static ConfigEntry<float> ConfigAssumedPlayerMassKg = null!;

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
                "Turns the mod on or off (requires a restart).");

            ConfigToggleKey = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.M),
                "Keyboard key to attach/detach mag boots.");

            ConfigToggleButton = Config.Bind("General", "ControllerToggleButton", InputControlType.DPadDown,
                "Controller button to attach/detach mag boots.");

            ConfigDebugPrint = Config.Bind("General", "DebugPrint", false,
                "Prints extra troubleshooting info to the log. Leave off unless asked to turn it on.");

            ConfigMaxAttachDistance = Config.Bind("Tuning", "MaxAttachDistance", 3f,
                "How far below your feet (in meters) mag boots will look for something to attach to.");

            ConfigMinFaceArea = Config.Bind("Tuning", "MinFaceArea", 2f,
                "How big a surface needs to be (in square meters) before you can attach to it. Keeps you from sticking to tiny brackets and pipes.");

            ConfigMaxNormalAngle = Config.Bind("Tuning", "MaxNormalAngle", 50f,
                "How tilted a surface can be (in degrees) and still count as \"flat enough\" to attach to.");

            ConfigStandoffDistance = Config.Bind("Tuning", "StandoffDistance", 1.5f,
                "How far off the surface (in meters) you float once attached.");

            ConfigSnapDuration = Config.Bind("Tuning", "SnapDuration", 1f,
                "How long (in seconds) the initial snap into place takes.");

            ConfigAheadCastDistance = Config.Bind("Tuning", "AheadCastDistance", 0.85f,
                "Your stride length while walking (in meters) - how far ahead mag boots checks for the next foothold.");

            ConfigMoveSpeed = Config.Bind("Tuning", "MoveSpeed", 2f,
                "Walking speed (in meters/second) while attached.");

            ConfigCornerSmoothingSpeed = Config.Bind("Tuning", "CornerSmoothingSpeed", 3f,
                "How quickly you lean into a sharp corner or step, instead of snapping right into the new angle. Lower is smoother/slower; higher is snappier.");

            ConfigReorientSettledAngle = Config.Bind("Tuning", "ReorientSettledAngle", 1f,
                "How closely you need to finish leaning into a new angle before taking the next step. Lower is stricter (smoother, but pauses more); higher is looser.");

            ConfigSpring = Config.Bind("Tuning", "Spring", 200f,
                "How firmly mag boots pull you back to the surface if you drift away. Higher is snappier.");

            ConfigDamper = Config.Bind("Tuning", "Damper", 30f,
                "Smooths out that pull-back so it doesn't bounce or overshoot. Higher is calmer.");

            ConfigBreakawayVelocity = Config.Bind("Tuning", "BreakawayVelocity", 10f,
                "How hard you need to be hit (in meters/second) before mag boots let go instead of holding on. Set very high to basically never let go.");

            ConfigMaxLookDownAngle = Config.Bind("Tuning", "MaxLookDownAngle", 45f,
                "How far you can look down toward the surface before your view is stopped, so you can't tip over and stare at your own feet.");

            ConfigBatteryCapacityMinutes = Config.Bind("Tuning", "BatteryCapacityMinutes", 5f,
                "How many minutes of attached time you get per shift before the battery runs out. Refills at the start of every shift. Set to 0 for unlimited.");

            ConfigBatteryDisplayMode = Config.Bind("HUD", "BatteryDisplayMode", BatteryDisplayMode.Gauge,
                "How the battery is shown on screen: Gauge (bar), Percentage, Timer (minutes:seconds), or Off (hidden).");

            ConfigHudOffset = Config.Bind("HUD", "Offset", "(0, -48)",
                "Moves the on-screen hint from the center of your screen, as a percent of your screen size. " +
                "Format: (x, y). e.g. (0, -48) moves it to the bottom-center. Negative x moves it left, negative y moves it down.");

            ConfigHudScale = Config.Bind("HUD", "Scale", 1f,
                "Size of the on-screen hint. 1 is the default size, 2 is twice as big, 0.5 is half as big.");

            ConfigRecoilEnabled = Config.Bind("Recoil", "Enabled", false,
                "Turns on extra kickback for the Cutter and Grapple Gun. Off by default.");

            ConfigSawCutterRecoil = Config.Bind("Recoil", "SawCutterRecoil", 0.25f,
                "How hard the saw Cutter kicks you back when you fire it. 0 turns it off.");

            ConfigScalpelCutterRecoil = Config.Bind("Recoil", "ScalpelCutterRecoil", 2f,
                "How hard the Scalpel/single-laser pushes you back while it's firing. 0 turns it off.");

            ConfigGrappleThrowRecoilMultiplier = Config.Bind("Recoil", "GrappleThrowRecoilMultiplier", 1f,
                "How hard you get pushed back when trying to throw a grappled object that's too heavy to move. 1 is normal, 0 turns it off.");

            ConfigGrappleReflectionRecoil = Config.Bind("Recoil", "GrappleReflectionRecoil", 0.25f,
                "How hard pushing or throwing something with the Grapple Gun kicks you back - stronger the closer and heavier the object is. 0 turns it off.");

            ConfigGrappleReflectionDistance = Config.Bind("Recoil", "GrappleReflectionDistance", 12f,
                "How far away (in meters) an object can be and still kick you back when pushed or thrown.");

            ConfigAssumedPlayerMassKg = Config.Bind("Recoil", "AssumedPlayerMassKg", 175f,
                "Your assumed weight (in kg), used to figure out how much of a push's force you feel versus the object.");

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
            if (!IsInFlightGameplay)
                return;

            _controller?.FixedUpdate();
            RecoilTuning.ScalpelFixedUpdate();
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
                ConfigBatteryDisplayMode.Value, _controller.BatteryFraction, _controller.BatteryMinutesRemaining, ConfigHudScale.Value);
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
