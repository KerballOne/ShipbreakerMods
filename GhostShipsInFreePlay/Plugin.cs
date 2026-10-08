using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace GhostShipsInFreePlay
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> ForceGhostShip = null!;
        internal static ConfigEntry<bool> IncludeCareerOnlyModules = null!;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey = null!;
        internal static ConfigEntry<InputControlType> ToggleControllerButton = null!;
        internal static ConfigEntry<string> IndicatorText = null!;
        internal static ConfigEntry<float> ToggleOffsetX = null!;
        internal static ConfigEntry<float> ToggleOffsetY = null!;
        internal static ConfigEntry<bool> DebugLogging = null!;

        private void Awake()
        {
            Log = Logger;

            ForceGhostShip = Config.Bind(
                "General", "ForceGhostShip", false,
                "State of the \"Ghost Ship\" checkbox on the FreePlay ship selection screen. When " +
                "true, every FreePlay ship launched loads as a ghost ship -- the same property set " +
                "career mode uses for its scrambled-thumbnail ghost ship cards (AI nodes, ghost " +
                "lighting and skin). The ghost property is always forced; career's 15% roll doesn't " +
                "apply. When false, FreePlay is completely stock. Normally toggled from the " +
                "checkbox rather than edited here.");

            IncludeCareerOnlyModules = Config.Bind(
                "General", "IncludeCareerOnlyModules", true,
                "Only applies to ghost ships. Some ship modules, hardpoints and scripted spawns " +
                "(including the ghost ship's AI nodes) are flagged to be left out of FreePlay. When " +
                "true, the ghost ship keeps them, matching the career ghost variant. Set false to " +
                "keep stock FreePlay stripping (the ship will then have no AI nodes).");

            ToggleKey = Config.Bind(
                "UI", "ToggleKey", new KeyboardShortcut(KeyCode.G),
                "Keyboard shortcut that flips the Ghost Ship checkbox while the FreePlay ship " +
                "selection screen is open.");

            ToggleControllerButton = Config.Bind(
                "UI", "ToggleControllerButton", InputControlType.DPadDown,
                "Controller button that flips the Ghost Ship checkbox while the FreePlay ship " +
                "selection screen is open. Used (and shown on the checkbox) instead of ToggleKey " +
                "whenever the last input came from a controller.");

            IndicatorText = Config.Bind(
                "UI", "GhostVariantTag", "<size=75%><color=#FF8C1A>(GHOST)</color></size>",
                "Shown after a ship's name (two spaces are added in front automatically) in the " +
                "FreePlay ship list when the Ghost Ship checkbox is checked and that ship has a " +
                "career ghost variant. TextMeshPro rich text.");

            ToggleOffsetX = Config.Bind(
                "UI", "OffsetFromBottomCenterX", 0f,
                "Horizontal offset of the checkbox from the bottom center of the screen, in UI units.");

            ToggleOffsetY = Config.Bind(
                "UI", "OffsetFromBottomCenterY", 40f,
                "Vertical offset of the checkbox from the bottom center of the screen, in UI units.");

            DebugLogging = Config.Bind(
                "Debug", "DebugLogging", false,
                "Extra logging for troubleshooting: every career property set found while looking " +
                "up ghost variants, which ships got the (GHOST) tag, generation details, and each " +
                "property-driven trigger on a ghost ship. The one-line [GhostStatus] report per " +
                "ship spawn is always logged regardless of this setting.");

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            GhostStatusLogger.Register();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded. ForceGhostShip={ForceGhostShip.Value}");
        }

        // Same detection MagBoots uses: whichever device produced the last game input.
        private static bool IsControllerActive =>
            LynxControls.Instance != null &&
            LynxControls.Instance.LastInputType == BindingSourceType.DeviceBindingSource;

        private void Update()
        {
            if (!FreePlayToggleUI.IsVisible) return;

            var controller = IsControllerActive;
            FreePlayToggleUI.Refresh(controller ? GetControllerButtonLabel(ToggleControllerButton.Value) : ToggleKey.Value.ToString());

            var pressed = controller
                ? InputManager.ActiveDevice?[ToggleControllerButton.Value].WasPressed ?? false
                : ToggleKey.Value.IsDown();
            if (pressed) ForceGhostShip.Value = !ForceGhostShip.Value;
        }

        // Mirrors MagBoots' label switch (Xbox vs PlayStation naming from the active device), plus
        // spaced D-pad names.
        private static string GetControllerButtonLabel(InputControlType button)
        {
            var style = InputManager.ActiveDevice?.DeviceStyle;
            bool isPlayStation = style == InputDeviceStyle.PlayStation3 ||
                                 style == InputDeviceStyle.PlayStation4 ||
                                 style == InputDeviceStyle.PlayStation5;

            return button switch
            {
                InputControlType.Action1 => isPlayStation ? "Cross" : "A",
                InputControlType.Action2 => isPlayStation ? "Circle" : "B",
                InputControlType.Action3 => isPlayStation ? "Square" : "X",
                InputControlType.Action4 => isPlayStation ? "Triangle" : "Y",
                InputControlType.Start => isPlayStation ? "Options" : "Start",
                InputControlType.DPadUp => "DPad Up",
                InputControlType.DPadDown => "DPad Down",
                InputControlType.DPadLeft => "DPad Left",
                InputControlType.DPadRight => "DPad Right",
                var other => other.ToString(),
            };
        }

        internal static void Debug(string msg)
        {
            if (DebugLogging.Value) Log.LogInfo(msg);
        }
    }
}
