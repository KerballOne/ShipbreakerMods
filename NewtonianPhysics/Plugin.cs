using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace NewtonianPhysics
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> ConfigEnabled = null!;
        internal static ConfigEntry<bool> ConfigDebugPrint = null!;

        internal static ConfigEntry<bool> ConfigNoBrakes = null!;

        internal static ConfigEntry<bool> ConfigRecoilEnabled = null!;
        internal static ConfigEntry<float> ConfigBrakeBreak = null!;
        internal static ConfigEntry<float> ConfigSawCutterRecoil = null!;
        internal static ConfigEntry<float> ConfigScalpelCutterRecoil = null!;
        internal static ConfigEntry<float> ConfigGrappleThrowRecoilMultiplier = null!;
        internal static ConfigEntry<float> ConfigGrappleReflectionRecoil = null!;
        internal static ConfigEntry<float> ConfigGrappleReflectionDistance = null!;
        internal static ConfigEntry<float> ConfigAssumedPlayerMassKg = null!;

        private void Awake()
        {
            Log = Logger;

            ConfigEnabled = Config.Bind("General", "Enabled", true,
                "Turns the mod on or off (requires a restart).");

            ConfigDebugPrint = Config.Bind("General", "DebugPrint", false,
                "Prints extra troubleshooting info to the log. Leave off unless asked to turn it on.");

            ConfigNoBrakes = Config.Bind("Newtonian", "NoBrakes", true,
                "Disables your air brake entirely. Braking on demand is a bit overpowered for a zero-g game - turning it off makes movement more realistically Newtonian: you keep drifting unless something (like recoil, the Grapple Gun pulling you, grabbing something by hand, or MagBoots) actually stops you.");

            ConfigRecoilEnabled = Config.Bind("Recoil", "Enabled", true,
                "Turns on extra kickback for the Cutter and Grapple Gun.");

            ConfigBrakeBreak = Config.Bind("Recoil", "BrakeBreak", 1f,
                "Whenever recoil kicks you back, your air brake is disabled for this many seconds afterward, so the kick actually moves you instead of being cancelled out instantly. Set to 0 to disable. Has no extra effect if NoBrakes is already on.");

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
                Log.LogInfo("NewtonianPhysics is disabled via config.");
                return;
            }

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            Log.LogInfo("NewtonianPhysics loaded.");
        }

        private void FixedUpdate()
        {
            if (GameSession.CurrentGameState != GameSession.GameState.Gameplay)
                return;

            RecoilTuning.ScalpelFixedUpdate();
        }
    }
}
