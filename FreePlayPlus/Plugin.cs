using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace FreePlayPlus
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static Plugin Instance = null!;

        internal static ConfigEntry<bool> ForceGhostShip = null!;
        internal static ConfigEntry<bool> IncludeCareerOnlyModules = null!;
        internal static ConfigEntry<bool> FreePlayHealthDamage = null!;
        internal static ConfigEntry<bool> ExitToMenuOnDeath = null!;
        internal static ConfigEntry<bool> ConsumeOxygen = null!;
        internal static ConfigEntry<bool> ConsumeFuel = null!;
        internal static ConfigEntry<bool> ConsumeDemoCharges = null!;
        internal static ConfigEntry<bool> ConsumeTethers = null!;
        internal static ConfigEntry<float> RefillSeconds = null!;
        internal static ConfigEntry<bool> StartAtZeroDebt = null!;
        internal static ConfigEntry<float> AutosavePoints = null!;
        internal static ConfigEntry<int> Autosaves = null!;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey = null!;
        internal static ConfigEntry<InputControlType> ToggleControllerButton = null!;
        internal static ConfigEntry<string> IndicatorText = null!;
        internal static ConfigEntry<float> ToggleOffsetX = null!;
        internal static ConfigEntry<float> ToggleOffsetY = null!;
        internal static ConfigEntry<bool> DebugLogging = null!;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            MigrateOldConfig();

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

            FreePlayHealthDamage = Config.Bind(
                "General", "FreePlayHealthDamage", true,
                "Applies to every FreePlay ship, ghost or not. Stock FreePlay makes you invulnerable. " +
                "When true, you take health damage from impacts and hazards (decompression, " +
                "explosions, crushing, leaving the play area) like in career, and suit repair " +
                "pickups found on the ship heal you. Oxygen stays unlimited as in stock FreePlay. " +
                "Set false for stock FreePlay invulnerability.");

            ExitToMenuOnDeath = Config.Bind(
                "General", "ExitToMenuOnDeath", true,
                "When you die in FreePlay, acknowledging the death report takes you back to the " +
                "FreePlay ship select instead of respawning on the same ship. (In career a death " +
                "costs a clone fee; FreePlay has no currency, so this makes death end the run.) " +
                "Set false to respawn on the ship as stock.");

            ConsumeOxygen = Config.Bind(
                "Consumables", "ConsumeOxygen", true,
                "FreePlay oxygen drains like in career instead of being unlimited. Refill at the " +
                "kiosk or in pressurised areas. With FreePlayHealthDamage also on, running out " +
                "suffocates you.");

            ConsumeFuel = Config.Bind(
                "Consumables", "ConsumeFuel", true,
                "FreePlay thruster fuel drains like in career instead of being unlimited. Refill at the kiosk.");

            ConsumeDemoCharges = Config.Bind(
                "Consumables", "ConsumeDemoCharges", true,
                "FreePlay demo charges are limited to career's carry amount instead of unlimited. Buy more at the kiosk.");

            ConsumeTethers = Config.Bind(
                "Consumables", "ConsumeTethers", true,
                "FreePlay tethers are limited to career's carry amount instead of 1000. Buy more at the kiosk.");

            RefillSeconds = Config.Bind(
                "Consumables", "RefillSeconds", 5f,
                new ConfigDescription(
                    "How long, in seconds, an oxygen or fuel purchase at the FreePlay kiosk takes to " +
                    "fill. You pay immediately; the refill only runs while the kiosk screen is open " +
                    "and pauses if you leave (it resumes when you come back, no need to buy again). " +
                    "0 = instant, as stock. Pickups found on the ship are always instant.",
                    new AcceptableValueRange<float>(0f, 60f)));

            StartAtZeroDebt = Config.Bind(
                "Consumables", "StartAtZeroDebt", true,
                "FreePlay uses a throwaway profile that starts with career's $1.25B debt, which the " +
                "kiosk shows. When true, FreePlay starts at $0 and kiosk purchases count up from there. " +
                "Your career profile is never affected either way.");

            AutosavePoints = Config.Bind(
                "Saves", "AutosavePoints", 10f,
                new ConfigDescription(
                    "FreePlay autosaves the ship each time salvage progress passes another multiple of " +
                    "this percent (10 = at 10%, 20%, 30%...). 0 = no autosaves. Saves are listed when you " +
                    "pick the ship on the FreePlay ship select.",
                    new AcceptableValueRange<float>(0f, 100f)));

            Autosaves = Config.Bind(
                "Saves", "Autosaves", 3,
                new ConfigDescription(
                    "How many autosaves to keep per ship; older autosaves are deleted. Manual saves (the " +
                    "Save button on the Hab door prompt) and exit saves are kept until you delete them. " +
                    "Each ship keeps at most 20 saves in total; past that the oldest is deleted.",
                    new AcceptableValueRange<int>(0, 20)));

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


            // Per class rather than PatchAll(), so one patch whose target moved in a game update
            // is logged and skipped instead of aborting every patch after it.
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), inherit: false)) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception ex) { Log.LogError($"Patch {type.Name} failed; that feature is disabled: {ex.GetBaseException().Message}"); }
            }
            ShipSpawnEvents.Register();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded. ForceGhostShip={ForceGhostShip.Value}");
        }

        // Same detection MagBoots uses: whichever device produced the last game input.
        private static bool IsControllerActive =>
            LynxControls.Instance != null &&
            LynxControls.Instance.LastInputType == BindingSourceType.DeviceBindingSource;

        private void Update()
        {
            CustomShipAssetLocator.TryInstall();
            FreePlayDamage.Tick();
            TimedRefill.Tick();
            ShipSaver.Tick();
            SaveListUI.Tick();
            if (!FreePlayToggleUI.IsVisible) return;

            var controller = IsControllerActive;
            FreePlayToggleUI.Refresh(controller ? GetControllerButtonLabel(ToggleControllerButton.Value) : ToggleKey.Value.ToString());

            if (SaveListUI.IsOpen || SaveListUI.Preparing) return; // the popup / preparing panel owns input
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

        // This mod was released as GhostShipsInFreePlay (plugin ID me.kerballone.GhostShipsInFreePlay).
        // Section/key names are unchanged, so a first run under the new ID starts from a copy of the
        // old config instead of defaults. The old file is left in place.
        private void MigrateOldConfig()
        {
            var oldPath = Path.Combine(Paths.ConfigPath, "me.kerballone.GhostShipsInFreePlay.cfg");
            if (File.Exists(Config.ConfigFilePath) || !File.Exists(oldPath)) return;
            File.Copy(oldPath, Config.ConfigFilePath);
            Config.Reload();
            Log.LogInfo("Copied settings from the old GhostShipsInFreePlay config.");
        }

        internal static void Debug(string msg)
        {
            if (DebugLogging.Value) Log.LogInfo(msg);
        }
    }
}
