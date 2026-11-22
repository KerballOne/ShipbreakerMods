using System;
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

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            CustomShips.Install(harmony);
            RoomPressure.Install(harmony);

            Main.EventSystem.AddHandler<LevelLoadEvent>(OnLevelLoad);
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        private void LateUpdate()
        {
            try { RoomPressure.Tick(); }
            catch (Exception ex) { Log.LogError("[RoomPressure] " + ex); }
            try { PartSeparation.Tick(); }
            catch (Exception ex) { Log.LogError("[Parts] " + ex); }
        }

        // Start is posted before the scenes load.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State != LevelLoadEvent.LoadState.Start) return;
            PartSeparation.CustomShip = false;
            // Pick up edits made to the settings file while the game was running, before the next ship loads.
            try { Instance.Config.Reload(); }
            catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
            RoomPressure.OnLevelLoadStart();
        }
    }
}
