using System;
using System.IO;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace TheExpanse
{
    // Changes to ships, for large custom ships:
    //   RoomPressure    makes each room's own pressurization / decompression time count, so big rooms take longer
    //   PartSeparation  on custom ships, heavy parts that overlap their neighbors come free gently instead of being thrown
    //   ShipRoll        on custom ghost ships, a haunting action of its own: the ship rolls slowly about its long axis
    //   NodeRadiation   on custom ghost ships, being near a node hurts, as radiation
    // (Centering ships in the bay and moving the jacks, which started here, now live in the BiggerBays mod; the Mars
    // planet, loading screen and menu pictures live in the Themes mod.)
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> Enabled = null!;
        internal static ConfigEntry<bool> RoomPressureTimes = null!;
        internal static ConfigEntry<bool> ExtendPressureEffects = null!;
        internal static ConfigEntry<float> PressureEffectSeconds = null!;
        internal static ConfigEntry<bool> GentleSeparation = null!;
        internal static ConfigEntry<bool> RollShip = null!;
        internal static ConfigEntry<float> RollDegreesPerSecond = null!;
        internal static ConfigEntry<float> RollSeconds = null!;
        internal static ConfigEntry<float> RollMinInterval = null!;
        internal static ConfigEntry<float> RollMaxInterval = null!;
        internal static ConfigEntry<float> RollStartShare = null!;
        internal static ConfigEntry<float> RollFrequencyRamp = null!;
        internal static ConfigEntry<float> RollSpeedRamp = null!;
        internal static ConfigEntry<bool> LightFlicker = null!;
        internal static ConfigEntry<bool> LightBlackout = null!;
        internal static ConfigEntry<float> LightMinInterval = null!;
        internal static ConfigEntry<float> LightMaxInterval = null!;
        internal static ConfigEntry<float> LightFrequencyRamp = null!;
        internal static ConfigEntry<float> LightRoomRadius = null!;
        internal static ConfigEntry<float> FlickerSeconds = null!;
        internal static ConfigEntry<float> BlackoutMinSeconds = null!;
        internal static ConfigEntry<float> BlackoutMaxSeconds = null!;
        internal static ConfigEntry<bool> HelmetFlicker = null!;
        internal static ConfigEntry<float> HelmetFlickerRadius = null!;
        internal static ConfigEntry<bool> RemoveGrowthAfterLastNode = null!;
        internal static ConfigEntry<bool> NodeRadiation = null!;
        internal static ConfigEntry<bool> TestTools = null!;
        internal static ConfigEntry<KeyboardShortcut> KillHalfNodesKey = null!;
        internal static ConfigEntry<float> NodeRadiationDamage = null!;
        internal static ConfigEntry<float> NodeRadiationRadius = null!;
        internal static ConfigEntry<float> NodeRadiationFalloff = null!;
        internal static ConfigEntry<bool> DrivePlume = null!;
        internal static ConfigEntry<string> PlumeAt = null!;
        internal static ConfigEntry<float> PlumeLength = null!;
        internal static ConfigEntry<float> PlumeMouthDiameter = null!;
        internal static ConfigEntry<float> PlumeSpread = null!;
        internal static ConfigEntry<float> PlumeBrightness = null!;
        internal static ConfigEntry<UnityEngine.Color> PlumeColor = null!;
        internal static ConfigEntry<float> PlumeTurbulence = null!;
        internal static ConfigEntry<float> PlumeFlowSpeed = null!;
        internal static ConfigEntry<float> PlumeSheath = null!;
        internal static ConfigEntry<float> PlumeCore = null!;
        internal static ConfigEntry<float> PlumeCoreDiameter = null!;
        internal static ConfigEntry<int> PlumeShockDiamonds = null!;
        internal static ConfigEntry<float> PlumeStreak = null!;
        internal static ConfigEntry<float> PlumeLightLumens = null!;
        internal static ConfigEntry<float> PlumeTestThrottle = null!;

        const float HauntingSeconds = 0.5f;
        static float sHauntingDue;

        private FileSystemWatcher? _watcher;
        static volatile bool sSettingsFileChanged;
        static float sSettingsFileChangedAt;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enable or disable TheExpanse.");

            RoomPressureTimes = Config.Bind("Rooms", "UsePressureTimes", true,
                "Make each room's own Pressurization Time and Decompression Time count. The game ignores them and changes every room's " +
                "pressure in the same few seconds; with this on, a room whose time is set longer than the game's takes that long. Connected " +
                "rooms change together and take the longest time among them. Rooms at or below the game's own time, and hull breaches, " +
                "behave as stock.");
            ExtendPressureEffects = Config.Bind("Rooms", "ExtendPressureEffects", true,
                "With UsePressureTimes on: keep an atmosphere regulator's hiss and vent effect going for as long as a slowed pressure " +
                "change lasts. They are a fixed few seconds long, so they are started again at even intervals until the change ends. " +
                "The spoken line is not repeated. Off: they play once and stop early, as they do without this setting.");
            PressureEffectSeconds = Config.Bind("Rooms", "PressureEffectSeconds", 2f,
                "With ExtendPressureEffects on: how many seconds one play of the regulator's hiss and vent effect covers. They are started " +
                "again no further apart than this. Lower it if you notice pauses between repeats; raise it if they pile up on each other.");

            GentleSeparation = Config.Bind("Parts", "GentleSeparation", true,
                "On custom ships, stop heavy parts being thrown when they are cut or yanked free. Parts collide as the convex shape " +
                "around their mesh, so on many custom ships neighboring parts overlap, and the game pushes overlapping parts apart at " +
                "up to 10 m/s the moment they separate. With this on, that push is limited by mass: parts of 5 tonnes or more are " +
                "eased apart at 0.3 m/s, parts up to 100 kg keep the game's speed, and parts in between fall on a smooth slide. The " +
                "grapple, tethers, explosions and decompression are not changed. The game's own ships are left alone.");

            RollShip = Config.Bind("Haunting", "RollShip", true,
                "On custom ghost ships, add a haunting action to the game's own three (doors, room pressure, electricity): now and " +
                "then the ship rolls slowly about its long axis, pushing loose parts and you with it. It stops for good when the last " +
                "node is destroyed. The game's own haunting is not changed.");
            RollDegreesPerSecond = Config.Bind("Haunting", "RollDegreesPerSecond", 2f,
                "How fast the ship turns during a roll, in degrees per second. The speed eases in and out over 3 seconds.");
            RollSeconds = Config.Bind("Haunting", "RollSeconds", 20f,
                "How long one roll lasts, in seconds. Speed times this is roughly how far the ship turns each time.");
            RollMinInterval = Config.Bind("Haunting", "RollMinInterval", 180f,
                "The shortest wait between rolls, in seconds. Each wait is a random time between this and RollMaxInterval.");
            RollMaxInterval = Config.Bind("Haunting", "RollMaxInterval", 480f,
                "The longest wait between rolls, in seconds.");
            RollStartShare = Config.Bind("Haunting", "RollStartShare", 1f,
                new ConfigDescription("Rolls happen while the share of nodes left is at or below this. 1: from the start of the shift. " +
                "0.5: only once half the nodes are destroyed, like the game's own haunting, which grows as nodes are lost.",
                new AcceptableValueRange<float>(0f, 1f)));

            RollFrequencyRamp = Config.Bind("Haunting", "RollFrequencyRamp", 3f,
                "Rolls come more often as nodes are destroyed: with the last node left, the waits are this many times shorter than " +
                "RollMinInterval and RollMaxInterval, and in between they shorten evenly with the share of nodes destroyed. 1: no change.");
            RollSpeedRamp = Config.Bind("Haunting", "RollSpeedRamp", 5f,
                "Rolls get faster as nodes are destroyed: with the last node left, the ship turns this many times faster than " +
                "RollDegreesPerSecond, so each roll turns further. In between the speed rises evenly. 1: no change.");

            LightFlicker = Config.Bind("Haunting", "LightFlicker", true,
                "On custom ghost ships, another haunting action: now and then the lights around one spot of the ship flicker for a " +
                "few seconds. Shares its timer with LightBlackout; with both on, each time is one or the other at random.");
            LightBlackout = Config.Bind("Haunting", "LightBlackout", true,
                "On custom ghost ships, another haunting action: now and then the lights around one spot of the ship go dark for a " +
                "while, stuttering as they go and as they come back.");
            LightMinInterval = Config.Bind("Haunting", "LightMinInterval", 60f,
                "The shortest wait between light hauntings, in seconds. Each wait is a random time between this and LightMaxInterval.");
            LightMaxInterval = Config.Bind("Haunting", "LightMaxInterval", 180f,
                "The longest wait between light hauntings, in seconds.");
            LightFrequencyRamp = Config.Bind("Haunting", "LightFrequencyRamp", 3f,
                "Light hauntings come more often as nodes are destroyed: with the last node left, the waits are this many times " +
                "shorter. 1: no change.");
            LightRoomRadius = Config.Bind("Haunting", "LightRoomRadius", 12f,
                "How far around the chosen spot lights are affected, in meters. About one room at the default.");
            FlickerSeconds = Config.Bind("Haunting", "FlickerSeconds", 4f,
                "How long a flicker lasts, in seconds.");
            BlackoutMinSeconds = Config.Bind("Haunting", "BlackoutMinSeconds", 10f,
                "The shortest blackout, in seconds. Each lasts a random time between this and BlackoutMaxSeconds.");
            BlackoutMaxSeconds = Config.Bind("Haunting", "BlackoutMaxSeconds", 30f,
                "The longest blackout, in seconds.");
            HelmetFlicker = Config.Bind("Haunting", "HelmetFlicker", true,
                "On custom ghost ships, the helmet light stutters near a node, more often and for longer the closer you get. Only " +
                "while it is switched on.");
            HelmetFlickerRadius = Config.Bind("Haunting", "HelmetFlickerRadius", 10f,
                "How close to a node the helmet light starts to stutter, in meters.");

            RemoveGrowthAfterLastNode = Config.Bind("Haunting", "RemoveGrowthAfterLastNode", true,
                "On custom ghost ships, the growth painted across the hull, walls and floors disappears when the last node is " +
                "destroyed, and stays gone if the ship is loaded from a save with no nodes left.");

            NodeRadiation = Config.Bind("Haunting", "NodeRadiation", true,
                "On custom ghost ships, being near a node damages your health as radiation. Radiation resistance counts. A node that is " +
                "destroyed stops radiating.");
            NodeRadiationDamage = Config.Bind("Haunting", "NodeRadiationDamage", 3f,
                "Health lost per second right at a node, as a percentage of full health. The doses of several nodes add up.");
            NodeRadiationRadius = Config.Bind("Haunting", "NodeRadiationRadius", 6f,
                "How far from a node its radiation reaches, in meters.");
            NodeRadiationFalloff = Config.Bind("Haunting", "NodeRadiationFalloff", 1f,
                "How the dose drops with distance inside the radius. 0: the same dose everywhere inside, which is how the game's own " +
                "radiation works. 1: falls evenly to nothing at the edge. 2 or more: strong close to the node, weak further out.");

            DrivePlume = Config.Bind("Drive", "DrivePlume", true,
                "Draw the MCRN Corvette's drive plume. For now it is always on.");
            PlumeAt = Config.Bind("Drive", "PlumeAt", "Bell",
                new ConfigDescription("Where the plume burns. Bell: out of the mouth of the ship's engine bell (its EngineNozzle parts if the " +
                    "bell is gone). MasterJack: a test spot 15 m above the bay's Master Jack, pointing up.",
                    new AcceptableValueList<string>("Bell", "MasterJack")));
            PlumeLength = Config.Bind("Drive", "Length", 150f,
                "How far the plume reaches from the engine bell at full throttle, in meters. Lower throttle shortens it in proportion.");
            PlumeTestThrottle = Config.Bind("Drive", "TestThrottle", 0.25f,
                "For testing until the flight controls drive it: the throttle the plume burns at, 0.01 to 1. Length and FlowSpeed scale with it.");
            PlumeMouthDiameter = Config.Bind("Drive", "MouthDiameter", 0f,
                "How wide the base plume is where it leaves the engine bell, in meters. 0: as wide as the bell's mouth, measured on the ship " +
                "(also at the test spot; 5 m there until the bell is found).");
            PlumeSpread = Config.Bind("Drive", "Spread", 1.4f,
                "How much wider the plume gets downstream: its widest, as a multiple of MouthDiameter. 1 keeps it a straight beam.");
            PlumeBrightness = Config.Bind("Drive", "Brightness", 1f,
                "How bright the whole plume glows. Core and Sheath set the two parts against each other.");
            PlumeColor = Config.Bind("Drive", "Color", new UnityEngine.Color(0.12f, 0.35f, 1f, 1f),
                "The color of the base plume; the core burns toward white. Keep red and green low or it washes out to white. The mouth glow takes a new color on the next level load.");
            PlumeTurbulence = Config.Bind("Drive", "Turbulence", 0.6f,
                "How much the sheath billows, 0 to 1. 0: smooth.");
            PlumeSheath = Config.Bind("Drive", "Sheath", 0.6f,
                "How bright the base plume is: the blue glow filling the bell's mouth around the core. Above about 1 it washes out to " +
                "white. 0: core only.");
            PlumeCore = Config.Bind("Drive", "Core", 4f,
                "How bright the white-hot core down the middle of the plume is, and its shock diamonds and the glow at the nozzle.");
            PlumeCoreDiameter = Config.Bind("Drive", "CoreDiameter", 3f,
                "How wide the white-hot core is at the bell, in meters.");
            PlumeFlowSpeed = Config.Bind("Drive", "FlowSpeed", 300f,
                "How fast the billows run down the plume at full throttle, in meters per second. Lower throttle slows them in proportion.");
            PlumeShockDiamonds = Config.Bind("Drive", "ShockDiamonds", 4,
                "How many bright shock diamonds sit in the core near the nozzle, 0 to 12.");
            PlumeStreak = Config.Bind("Drive", "Streak", 1f,
                "How bright the thin horizontal flare streak through the nozzle glow is. 0: none.");
            PlumeLightLumens = Config.Bind("Drive", "LightLumens", 150000f,
                "How much light the plume casts on the nozzle and the hull, in lumens.");

            TestTools = Config.Bind("Debug", "TestTools", false,
                "For testing the ghost ship additions: show how many nodes are left on screen, and destroy half of them with " +
                "KillHalfNodesKey.");
            KillHalfNodesKey = Config.Bind("Debug", "KillHalfNodesKey", new KeyboardShortcut(UnityEngine.KeyCode.F8),
                "With TestTools on: destroys half of the nodes left (rounded up, so the last one goes too).");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            CustomShips.Install(harmony);
            RoomPressure.Install(harmony);

            Main.EventSystem.AddHandler<LevelLoadEvent>(OnLevelLoad);
            Main.EventSystem.AddHandler<ShipAllSpawnWrapperEvent>(ev =>
            {
                if (ev.State == ShipAllSpawnWrapperEvent.SpawnState.Complete) GrowthCleanup.OnShipSpawned();
            });
            WatchSettingsFile();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        // ---- settings file ------------------------------------------------------------------------------------------
        // Saving the settings file while the game runs re-reads it within a second, for tuning during a shift.
        private void WatchSettingsFile()
        {
            try
            {
                string path = Config.ConfigFilePath;
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                };
                FileSystemEventHandler changed = (_, __) => sSettingsFileChanged = true;     // the watcher's own thread: no Unity calls
                _watcher.Changed += changed;
                _watcher.Created += changed;
                _watcher.Renamed += (_, __) => sSettingsFileChanged = true;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.LogWarning("Could not watch the settings file; changes apply when a ship loads: " + ex.Message); }
        }

        private void OnDestroy() => _watcher?.Dispose();

        private void OnGUI()
        {
            try { NodeTestTools.Draw(); }
            catch (Exception ex) { Log.LogError("[Test] " + ex); }
        }

        private void Update()
        {
            try { NodeTestTools.Tick(); }
            catch (Exception ex) { Log.LogError("[Test] " + ex); }
            // Wait for the editor to finish writing before reading the file.
            if (sSettingsFileChanged)
            {
                sSettingsFileChanged = false;
                sSettingsFileChangedAt = UnityEngine.Time.unscaledTime;
            }
            else if (sSettingsFileChangedAt > 0f && UnityEngine.Time.unscaledTime - sSettingsFileChangedAt > 0.5f)
            {
                sSettingsFileChangedAt = 0f;
                try
                {
                    Config.Reload();
                    ShipRoll.OnSettingsChanged();
                    HauntedLights.OnSettingsChanged();
                    Log.LogInfo("Settings file re-read; the new values apply now.");
                }
                catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
            }
        }

        private void LateUpdate()
        {
            try { RoomPressure.Tick(); }
            catch (Exception ex) { Log.LogError("[RoomPressure] " + ex); }
            try { PartSeparation.Tick(); }
            catch (Exception ex) { Log.LogError("[Parts] " + ex); }
            try { HauntingTick(); HauntedLights.Frame(); }
            catch (Exception ex) { Log.LogError("[Haunting] " + ex); }
            try { TheExpanse.DrivePlume.Frame(); }
            catch (Exception ex) { Log.LogError("[Drive] " + ex); }
        }

        // The ghost ship additions, a couple of times a second of game time (so not while paused).
        private static void HauntingTick()
        {
            if (UnityEngine.Time.time < sHauntingDue) return;
            sHauntingDue = UnityEngine.Time.time + HauntingSeconds;
            if (!Enabled.Value || !PartSeparation.CustomShip)
            {
                GhostNodes.Reset();
                ShipRoll.Schedule();            // with no nodes counted, this eases any roll under way to a stop
                HauntedLights.Schedule();       // and this puts any haunted lights back
                return;
            }
            GhostNodes.Refresh();
            TheExpanse.NodeRadiation.Apply(HauntingSeconds);
            ShipRoll.Schedule();
            HauntedLights.Schedule();
            GrowthCleanup.Tick();
        }

        private void FixedUpdate()
        {
            try { ShipRoll.FixedStep(); }
            catch (Exception ex) { Log.LogError("[Haunting] " + ex); }
        }

        // Start is posted before the scenes load.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State != LevelLoadEvent.LoadState.Start) return;
            PartSeparation.CustomShip = false;
            GhostNodes.Reset();
            ShipRoll.Reset();
            HauntedLights.Reset();
            GrowthCleanup.Reset();
            TheExpanse.DrivePlume.Reset();
            TheExpanse.NodeRadiation.Reset();
            sHauntingDue = 0f;
            // Pick up edits made to the settings file while the game was running, before the next ship loads.
            try { Instance.Config.Reload(); }
            catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
            RoomPressure.OnLevelLoadStart();
        }
    }
}
