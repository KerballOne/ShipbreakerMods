using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ViewNoLag
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> ConfigEnabled = null!;
        internal static ConfigEntry<float> ConfigSensitivity = null!;
        internal static ConfigEntry<bool> ConfigDebugPrint = null!;

        private void Awake()
        {
            Log = Logger;

            ConfigEnabled = Config.Bind("General", "Enabled", true,
                "Turns the mod on or off. When off, mouse-look uses vanilla's laggy zero-g drift.");

            ConfigSensitivity = Config.Bind("Tuning", "Sensitivity", 1f,
                "Multiplier on top of your normal in-game mouse/controller sensitivity settings. " +
                "Instant look can feel faster or slower than the old laggy version at the same sensitivity - adjust this to taste.");

            ConfigDebugPrint = Config.Bind("General", "DebugPrint", false,
                "Prints extra troubleshooting info to the log. Leave off unless asked to turn it on.");

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            Log.LogInfo("ViewNoLag loaded.");
        }
    }
}
