using System.IO;
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
        internal static ConfigEntry<bool> ConfigPlayerLinearDrag = null!;
        internal static ConfigEntry<bool> ConfigPlayerRotationDrag = null!;
        internal static ConfigEntry<bool> ConfigObjectDrag = null!;
        internal static ConfigEntry<float> ConfigMaxVelocityMps = null!;
        internal static ConfigEntry<float> ConfigWorkAreaRadiusMultiplier = null!;

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

            ConfigPlayerLinearDrag = Config.Bind("Newtonian", "PlayerLinearDrag", false,
                "Vanilla quietly slows you back down to a crawl over time whenever you're drifting and not actively thrusting, grabbing, or grappled - even with NoBrakes on. Leave this off to remove that ambient drag entirely, so you keep drifting at a constant velocity like real Newtonian motion until something actually stops you. Turn it on to restore vanilla's automatic slowdown.");

            ConfigPlayerRotationDrag = Config.Bind("Newtonian", "PlayerRotationDrag", false,
                "Vanilla damps out any tumble/spin you pick up whenever you're not actively steering. Leave this off to let spin persist indefinitely, just like linear drift. Turn it on to restore vanilla's automatic tumble damping.");

            ConfigObjectDrag = Config.Bind("Newtonian", "ObjectDrag", false,
                "Vanilla applies drag to loose parts and debris so they settle down over time instead of drifting/spinning forever. Leave this off to remove that drag so objects behave the same as the player - once moving, they keep moving. Turn it on to restore vanilla's object drag.");

            ConfigMaxVelocityMps = Config.Bind("Newtonian", "MaxVelocityMps", 40f,
                "Caps how fast you can drift, in meters per second. Vanilla's default is 20. Set to 0 for no cap at all.");

            ConfigWorkAreaRadiusMultiplier = Config.Bind("Newtonian", "WorkAreaRadiusMultiplier", 0f,
                "Scales how far you can roam from the game's designated work areas before the warning/danger zone (which can eventually teleport or hurt you) kicks in. 1 is vanilla, 2 doubles it, etc. Set to 0 to disable the work area limit entirely.");

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
            SetUpConfigFileWatcher();
            Log.LogInfo("NewtonianPhysics loaded.");
        }

        private FileSystemWatcher? _configFileWatcher;
        // Plain bool, not a Unity Time-based timestamp - the watcher's Changed event fires on a
        // background thread, and Unity's Time API can only be touched from the main thread. Update()
        // (main thread) polls this flag and does its own short real-world delay via a frame counter
        // instead, both to marshal safely and to debounce multiple rapid Changed events from one save.
        private volatile bool _configReloadPending;
        private int _configReloadDebounceFramesLeft;

        // BepInEx's ConfigFile never reloads itself when the .cfg is hand-edited on disk - without
        // this, every tuning change required a full game restart to take effect. FileSystemWatcher is
        // OS-level file notification (no polling, no per-tick cost), so this is free at runtime except
        // for the rare moment an edit is actually saved.
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
            _configFileWatcher?.Dispose();
        }

        // Same short frame-count delay (not time-based) MagBoots uses, restarted every time
        // OnConfigFileChangedOnDisk fires - debounces the multiple near-simultaneous Changed events
        // most editors trigger per save into a single Config.Reload() once the file has actually settled.
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
                    Log.LogInfo("NewtonianPhysics: config file changed on disk, reloaded live.");
                }
            }
        }

        private void FixedUpdate()
        {
            if (GameSession.CurrentGameState != GameSession.GameState.Gameplay)
                return;

            RecoilTuning.ScalpelFixedUpdate();
        }
    }
}
