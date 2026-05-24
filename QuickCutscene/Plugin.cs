using BBI.Unity.Game;
using BepInEx;
using InControl;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace QuickCutscene
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<bool> ConfigEnabled = null!;
        internal static ConfigEntry<KeyboardShortcut> ConfigSkipKey = null!;
        internal static ConfigEntry<InputControlType> ConfigSkipButton = null!;
        internal static ConfigEntry<bool> ConfigDebugPrint = null!;
        internal static bool IsSkippable;

        private static bool IsControllerActive =>
            LynxControls.Instance != null &&
            LynxControls.Instance.LastInputType == BindingSourceType.DeviceBindingSource;

        internal static bool ShouldSkip()
        {
            if (IsControllerActive)
                return InputManager.ActiveDevice?[ConfigSkipButton.Value].WasPressed ?? false;
            return ConfigSkipKey.Value.IsDown();
        }

        private GUIStyle? _hintStyle;

        private void Awake()
        {
            Log = Logger;

            ConfigEnabled = Config.Bind("General", "Enabled", true,
                "Enable or disable the plugin (requires restart).");

            ConfigSkipKey = Config.Bind("General", "SkipKey", new KeyboardShortcut(KeyCode.F6),
                "Keyboard shortcut to skip the current cutscene.");

            ConfigSkipButton = Config.Bind("General", "ControllerSkipButton", InputControlType.Action2,
                "Controller button to skip the current cutscene. Default is Action2 (B on Xbox / Circle on PlayStation). " +
                "Common values: Action1 (A/Cross), Action2 (B/Circle), Action3 (X/Square), Action4 (Y/Triangle), Start (Menu/Options).");

            ConfigDebugPrint = Config.Bind("General", "DebugPrint", false,
                "Log verbose debug info.");

            if (!ConfigEnabled.Value)
            {
                Log.LogInfo("QuickCutscene is disabled via config.");
                return;
            }

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            Log.LogInfo("QuickCutscene loaded. Press " + ConfigSkipKey.Value + " during an after-shift sequence to skip it.");
        }

        private void OnGUI()
        {
            if (!IsSkippable && !HabCustomGreetingController.HabGreetingShowing && GameSession.CurrentGameState != GameSession.GameState.NIS) return;

            _hintStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.LowerCenter,
            };

            string button = ConfigSkipButton.Value switch
            {
                InputControlType.Action1 => "A / Cross",
                InputControlType.Action2 => "B / Circle",
                InputControlType.Action3 => "X / Square",
                InputControlType.Action4 => "Y / Triangle",
                InputControlType.Start   => "Start / Options",
                var other                => other.ToString(),
            };
            string text = IsControllerActive ? $"Press {button} to skip" : $"Press {ConfigSkipKey.Value} to skip";
            var rect = new Rect(0, 0, Screen.width, Screen.height - 30);

            _hintStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), text, _hintStyle);
            _hintStyle.normal.textColor = Color.white;
            GUI.Label(rect, text, _hintStyle);
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID    = "me.kerballone.QuickCutscene";
        public const string PLUGIN_NAME    = "QuickCutscene";
        public const string PLUGIN_VERSION = "1.1.0";
    }
}
