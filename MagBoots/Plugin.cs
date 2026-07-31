using System;
using System.Globalization;
using System.IO;
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
        internal static ConfigEntry<float> ConfigAttachHeightFollowSpeed = null!;
        internal static ConfigEntry<float> ConfigStepLateralSettled = null!;
        internal static ConfigEntry<float> ConfigStepUpSettled = null!;
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
        internal static ConfigEntry<float> ConfigMaxStridePitch = null!;
        internal static ConfigEntry<float> ConfigMinStridePitch = null!;
        internal static ConfigEntry<bool> ConfigPreciseRotation = null!;
        internal static ConfigEntry<float> ConfigPreciseRotationSensitivity = null!;
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
        internal static ConfigEntry<bool> ConfigShowStride = null!;

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

            ConfigDebugPrint = Config.Bind("1 - General", "DebugPrint", false,
                "Prints extra troubleshooting info to the log. Leave off unless asked to turn it on.");

            ConfigToggleButton = Config.Bind("1 - General", "PowerToggle_ControllerButton", InputControlType.DPadDown,
                "Controller button to attach/detach mag boots.");

            ConfigToggleKey = Config.Bind("1 - General", "PowerToggle_Key", new KeyboardShortcut(KeyCode.M),
                "Keyboard key to attach/detach mag boots.");

            ConfigRunActivationMode = Config.Bind("1 - General", "Run_ActivationMode", RunActivationMode.Toggle,
                "How the run key/button works: Toggle means press once to start running and press again to stop; Hold means you only run while it's held down.");

            // Bound as a string rather than a second ConfigEntry<InputControlType> - see ParseRunControllerButton
            // for why (BepInEx would otherwise print the same huge enum value list a second time).
            ConfigRunToggleButton = Config.Bind("1 - General", "RunToggle_ControllerButton", InputControlType.LeftStickButton.ToString(),
                "Controller button to run while attached. Use the same names as PowerToggle_ControllerButton above (e.g. LeftStickButton).");

            ConfigRunToggleKey = Config.Bind("1 - General", "RunToggle_Key", new KeyboardShortcut(KeyCode.LeftShift),
                "Keyboard key to run while attached.");

            // Only settings governing the initial attach itself (the toggle-key raycast + snap tween into
            // place) - PlayerHeight and Angle_MaxNormal are ALSO read continuously afterward (the standoff
            // distance while walking, and the default non-forward-sweep surface tolerance during step
            // search, respectively), but this is their primary/originating purpose, so they live here.
            ConfigMaxNormalAngle = Config.Bind("2 - Attach", "Angle_MaxNormal", 20f,
                "How tilted a surface can be (in degrees) and still count as \"flat enough\" to attach to. Also used as the default (non-forward-facing) tolerance while walking - see Angle_MaxNormalFwd in Steps.");

            ConfigMinFaceArea = Config.Bind("2 - Attach", "MinFaceArea", 3f,
                "How big a surface needs to be (in square meters) before you can attach to it. Keeps you from sticking to tiny brackets and pipes.");

            ConfigPlayerHeight = Config.Bind("2 - Attach", "PlayerHeight", 1.5f,
                "How far off the surface (in meters) you float once attached. Also the ongoing standoff distance used every tick while walking, not just at the initial attach.");

            ConfigSnapDuration = Config.Bind("2 - Attach", "SnapDuration", 1f,
                "How long (in seconds) the initial snap into place takes.");

            // Only settings for ordinary walking/orientation to your next foothold - NOT step-specific
            // (see "4 - Steps" for detecting/handling an actual up or down transition).
            ConfigAheadCastDistance = Config.Bind("3 - Movement", "Distance_MaxStride", 0.85f,
                "Your stride length while walking (in meters) - how far ahead mag boots checks for the next foothold. Also the base search radius every step-candidate raycast is built from - see Steps.");

            ConfigMaxLookDownAngle = Config.Bind("3 - Movement", "Pitch_MaxLookDown", 45f,
                "How far you can look down toward the surface before your view is stopped, so you can't tip over and stare at your own feet.");

            ConfigMaxStridePitch = Config.Bind("3 - Movement", "Pitch_MaxStride", 10f,
                "How far you can pitch your view up or down (in degrees) before your stride starts shortening. Below this, looking around doesn't affect your stride at all.");

            ConfigMinStridePitch = Config.Bind("3 - Movement", "Pitch_MinStride", 60f,
                "The pitch angle (in degrees, up or down) at which your stride shrinks to its minimum (~0.01m, effectively stopped). Between Pitch_MaxStride and this, stride shortens gradually - pitching your view lets you take smaller, more careful steps on steep stairs.");

            ConfigCornerSmoothingSpeed = Config.Bind("3 - Movement", "Rate_CornerSmoothing", 3f,
                "How quickly you lean into a sharp corner, instead of snapping right into the new angle. Lower is smoother/slower; higher is snappier.");

            ConfigPreciseRotation = Config.Bind("3 - Movement", "Rotation_Precise", true,
                "While attached, look/turn instantly instead of using the game's normal zero-g drift/momentum - only affects yaw and pitch (roll doesn't apply while grounded). Turns off automatically the moment you detach.");

            ConfigPreciseRotationSensitivity = Config.Bind("3 - Movement", "Rotation_PreciseSensitivity", 0.25f,
                "Multiplier on top of your normal mouse/controller sensitivity settings, applied only while Rotation_Precise is active. Instant look can feel faster or slower than the drifting version at the same sensitivity - adjust this to taste.");

            ConfigReorientSettledAngle = Config.Bind("3 - Movement", "Settle_ReorientAngle", 1f,
                "How closely you need to finish leaning into a new angle before taking the next step. Lower is stricter (smoother, but pauses more); higher is looser.");

            ConfigMoveSpeed = Config.Bind("3 - Movement", "Speed_Walk", 2f,
                "Walking speed (in meters/second) while attached. Actual speed may run a bit higher than this target.");

            ConfigRunSpeed = Config.Bind("3 - Movement", "Speed_Run", 4f,
                "Running speed (in meters/second) while attached. Actual speed may run a bit higher than this target. Battery drains proportionally faster while running.");

            // Only settings for detecting and handling an actual step up or down transition while walking.
            ConfigFwdSweepAngle = Config.Bind("4 - Steps", "Angle_FwdSweep", 60f,
                "How wide a cone in front of you (in degrees) counts as \"forward\" for Angle_MaxNormalFwd and Height_StepUpFwd. 60 means 30 degrees to either side of dead ahead.");

            ConfigMaxNormalFwdAngle = Config.Bind("4 - Steps", "Angle_MaxNormalFwd", 50f,
                "A looser version of Angle_MaxNormal that only applies to footholds within Angle_FwdSweep in front of you - lets you walk up steeper ramps and inclines you're facing, while footholds off to the side or behind you still use the stricter Angle_MaxNormal.");

            ConfigStepDownHeight = Config.Bind("4 - Steps", "Height_StepDown", 1.5f,
                "How far below your feet (in meters) mag boots will look for something to attach to.");

            ConfigAttachHeightFollowSpeed = Config.Bind("4 - Steps", "Height_StepFollowVelocity", 2f,
                "How fast (in meters/second) mag boots eases your anchor's height toward a new surface for ordinary (non-significant) height changes, instead of snapping to it instantly. Keeps small floor seams/ledges or a change in surface angle from yanking you via the standoff spring. Lower is gentler; higher is snappier.");

            ConfigStepSignificantHeight = Config.Bind("4 - Steps", "Height_StepSignificant", 0.15f,
                "How big a height change (in meters) counts as a real step up or down versus just uneven flat ground. A real step DOWN past this waits for Settle_StepDown before adjusting your height, which keeps steep stairs from building up speed and knocking you loose. A real step UP past this instead eases in smoothly at Height_StepFollowVelocity right away - going up doesn't have the same runaway-speed risk going down does.");

            ConfigStepUpHeight = Config.Bind("4 - Steps", "Height_StepUp", 0.25f,
                "How far above your feet (in meters) mag boots will look for something to step up onto while already attached, for footholds off to the side or behind you. Lower makes it harder to accidentally step up onto something you didn't mean to.");

            ConfigStepUpFwdHeight = Config.Bind("4 - Steps", "Height_StepUpFwd", 0.95f,
                "A taller version of Height_StepUp that only applies to footholds within Angle_FwdSweep in front of you - lets you step up onto taller ledges and obstacles you're actually walking toward, while things off to the side or behind you still use the shorter Height_StepUp.");

            ConfigStepLateralSettled = Config.Bind("4 - Steps", "Settle_StepDown", 0.1f,
                "Only applies to stepping DOWN. How closely you need to catch up to a detected step before mag boots adjusts your height to match it, as a fraction of your stride length (Distance_MaxStride). 0.1 means within 10% of a stride. Moving forward isn't held up, only the up/down adjustment - this keeps steep stairs from feeling like a fast slide. Lower is stricter (closer catch-up needed); higher is looser.");

            ConfigStepUpSettled = Config.Bind("4 - Steps", "Settle_StepUp", 0.1f,
                "Only applies to stepping UP. How closely your height needs to catch up to a detected step before mag boots resumes forward movement, as a fraction of your stride length (Distance_MaxStride). 0.1 means within 10% of a stride. Height snaps immediately on a step up, but forward movement is held until this settles (or Timeout_Step elapses) - this keeps a steep step up from looking like teleporting forward and up in the same tick. Lower is stricter (closer catch-up needed, more delay before moving again); higher is looser (moves again sooner, especially useful on very steep stairs where catching up in height takes longer).");

            ConfigStepTimeout = Config.Bind("4 - Steps", "Timeout_Step", 0.3f,
                "Applies to both stepping DOWN and stepping UP. Maximum time (in seconds) mag boots will wait for you to catch up to a detected step before adjusting your position anyway (height for a step down, forward movement for a step up). Prevents ever getting stuck waiting, even if Settle_StepDown/Settle_StepUp is never reached (e.g. while continuously running).");

            ConfigBreakawayVelocity = Config.Bind("5 - Physics", "BreakawayVelocity", 12f,
                "How hard you need to be hit (in meters/second) before mag boots let go instead of holding on. Set very high to basically never let go.");

            ConfigDamper = Config.Bind("5 - Physics", "Damper", 30f,
                "Smooths out that pull-back so it doesn't bounce or overshoot. Higher is calmer.");

            ConfigSpring = Config.Bind("5 - Physics", "Spring", 200f,
                "How firmly mag boots pull you back to the surface if you drift away. Higher is snappier.");

            ConfigBatteryCapacityMinutes = Config.Bind("6 - Battery", "CapacityMinutes", 5f,
                "How many minutes of attached time you get per shift before the battery runs out. Refills at the start of every shift. Set to 0 for unlimited.");

            ConfigIdlePowerMultiplier = Config.Bind("6 - Battery", "PowerMultiplier_Idle", 0.1f,
                "How much battery you use while standing still and attached, compared to walking. 0.1 means standing still uses a tenth as much power as moving. 1 means standing still costs the same as moving.");

            ConfigLockingPowerMultiplier = Config.Bind("6 - Battery", "PowerMultiplier_Locking", 10f,
                "How much battery the initial snap-into-place takes, compared to normal attached use. At the default SnapDuration of 1 second and a multiplier of 10, snapping into place costs as much battery as 10 seconds of normal attached time.");

            ConfigBatteryDisplayMode = Config.Bind("7 - HUD", "BatteryDisplayMode", BatteryDisplayMode.Gauge,
                "How the battery is shown on screen: Gauge (bar), Percentage, Timer (minutes:seconds), or Off (hidden).");

            ConfigHudOffset = Config.Bind("7 - HUD", "Offset", "(0, -48)",
                "Moves the on-screen hint from the center of your screen, as a percent of your screen size. " +
                "Format: (x, y). e.g. (0, -48) moves it to the bottom-center. Negative x moves it left, negative y moves it down.");

            ConfigHudScale = Config.Bind("7 - HUD", "Scale", 1f,
                "Size of the on-screen hint. 1 is the default size, 2 is twice as big, 0.5 is half as big.");

            ConfigShowStride = Config.Bind("7 - HUD", "ShowStride", false,
                "Shows your current stride distance (in meters) next to the hint, which shortens as you pitch your view up or down (see Pitch_MaxStride/Pitch_MinStride).");

            if (!ConfigEnabled.Value)
            {
                Log.LogInfo("MagBoots is disabled via config.");
                return;
            }

            _controller = new MagBootsController();
            Main.EventSystem.AddHandler<GameStateChangedEvent>(OnGameStateChanged);

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            SetUpConfigFileWatcher();
            Log.LogInfo("MagBoots loaded. Press " + ConfigToggleKey.Value + " to attach/detach mag boots.");
        }

        private FileSystemWatcher? _configFileWatcher;
        // Plain bool, not a Unity Time-based timestamp - the watcher's Changed event fires on a
        // background thread, and Unity's Time API can only be touched from the main thread. Update()
        // (main thread) polls this flag and does its own short real-world delay via a frame counter
        // instead, both to marshal safely and to debounce multiple rapid Changed events from one save.
        private volatile bool _configReloadPending;
        private int _configReloadDebounceFramesLeft;

        // BepInEx's ConfigFile never reloads itself when the .cfg is hand-edited on disk (confirmed: no
        // built-in watcher in this BepInEx version, only a manual Reload() method) - without this, every
        // tuning change required a full game restart to take effect. FileSystemWatcher is OS-level file
        // notification (no polling, no per-tick cost), so this is free at runtime except for the rare
        // moment an edit is actually saved.
        private void SetUpConfigFileWatcher()
        {
            string directory = Path.GetDirectoryName(Config.ConfigFilePath)!;
            string fileName = Path.GetFileName(Config.ConfigFilePath);

            _configFileWatcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _configFileWatcher.Changed += OnConfigFileChangedOnDisk;
        }

        // Runs on a background thread (FileSystemWatcher's own thread) - must not touch any Unity API
        // (Time, GameObject, etc.) here, only plain .NET fields. Most editors trigger multiple Changed
        // events per save (e.g. one for the write, one for a metadata update); Update() below debounces
        // those into a single Reload() by restarting its own short frame-count delay every time this fires.
        private void OnConfigFileChangedOnDisk(object sender, FileSystemEventArgs e)
        {
            _configReloadPending = true;
        }

        private void OnDestroy()
        {
            Main.EventSystem.RemoveHandler<GameStateChangedEvent>(OnGameStateChanged);
            _configFileWatcher?.Dispose();
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

        // A short frame-count delay (not time-based) before actually reloading, restarted every time
        // OnConfigFileChangedOnDisk fires - debounces the multiple near-simultaneous Changed events most
        // editors trigger per save into a single Config.Reload() once the file has actually settled.
        private const int ConfigReloadDebounceFrames = 15;

        private void Update()
        {
            if (_configReloadPending)
            {
                _configReloadPending = false;
                _configReloadDebounceFramesLeft = ConfigReloadDebounceFrames;
            }

            if (_configReloadDebounceFramesLeft > 0)
            {
                _configReloadDebounceFramesLeft--;
                if (_configReloadDebounceFramesLeft == 0)
                {
                    Config.Reload();
                    Log.LogInfo("MagBoots: config file changed on disk, reloaded live.");
                }
            }

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
                ConfigBatteryDisplayMode.Value, _controller.BatteryFraction, _controller.BatteryMinutesRemaining, ConfigHudScale.Value,
                ConfigShowStride.Value && _controller.IsAttached, _controller.CurrentStrideDistance);
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
