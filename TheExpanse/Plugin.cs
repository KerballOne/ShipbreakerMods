using System;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace TheExpanse
{
    // Makes the work bay take oversized ships:
    //   JackMover       measures the ship after each level load and moves the Master Jack and the five light jacks clear of it
    //   VariantUnlock   gives weight-0 ship variants a spawn weight, so they only appear with this mod installed
    //   RoomPressure    makes each room's own pressurization / decompression time count, so big rooms take longer
    // (The Mars planet, loading screen and menu pictures that started here now live in the Themes mod.)
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> Enabled = null!;
        internal static ConfigEntry<float> ThresholdX = null!;
        internal static ConfigEntry<float> ThresholdY = null!;
        internal static ConfigEntry<float> ThresholdZ = null!;
        internal static ConfigEntry<float> Clearance = null!;
        internal static ConfigEntry<bool> MovePlayerSpawn = null!;
        internal static ConfigEntry<bool> RoomPressureTimes = null!;
        internal static ConfigEntry<string> VariantWeights = null!;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enable or disable TheExpanse.");

            const string th = "Size of the ship's overall bounding box along the bay's {0} axis, in metres, above which the jacks are moved. " +
                              "The ship's size is logged on every load, so stock ships can be used to calibrate this.";
            ThresholdX = Config.Bind("Thresholds", "MaxSizeX", 70f, string.Format(th, "X (width)"));
            ThresholdY = Config.Bind("Thresholds", "MaxSizeY", 45f, string.Format(th, "Y (height)"));
            ThresholdZ = Config.Bind("Thresholds", "MaxSizeZ", 100f, string.Format(th, "Z (length)"));

            Clearance = Config.Bind("Jacks", "ClearanceMeters", 5f,
                "Gap kept between an oversized ship's bounding box and the jacks. The Master Jack is pushed along +Z until it is this far past " +
                "the ship's far end; the five light jacks are raised together along +Y until the lowest is this far above the ship's top. " +
                "A jack that already has this much room is left where it is.");
            MovePlayerSpawn = Config.Bind("Jacks", "MovePlayerSpawnWithMasterJack", true,
                "Shift the player's start / respawn point by the same amount as the Master Jack, so shifts still start at its door.");

            VariantWeights = Config.Bind("Variants", "Weights", VariantUnlock.DefaultWeights,
                "Ship variants that only spawn with this mod installed. Each is shipped with Weight 0 in its ModuleEntryDefinition, so the " +
                "game never picks it; this mod gives it the weight listed here. Format: comma-separated name=weight, where name is the " +
                "file name of the variant's module prefab without the extension (the prefab its entry spawns), or the name of the entry " +
                "asset itself. Weights are relative to the other entries in the same list (a base ship at weight 1 and a variant at 1 is " +
                "a 50/50 pick). Default entry: the Tachi's Gas variant.");

            RoomPressureTimes = Config.Bind("Rooms", "UsePressureTimes", true,
                "Make each room's own Pressurization Time and Decompression Time count. The game ignores them and changes every room's " +
                "pressure in the same few seconds; with this on, a room whose time is set longer than the game's takes that long. Connected " +
                "rooms change together and take the longest time among them. Rooms at or below the game's own time, and hull breaches, " +
                "behave as stock.");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            VariantUnlock.Install(harmony);
            RoomPressure.Install(harmony);

            Main.EventSystem.AddHandler<LevelLoadEvent>(OnLevelLoad);
            Main.EventSystem.AddHandler<ShipAllSpawnWrapperEvent>(OnShipSpawnWrapper);
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        private void LateUpdate()
        {
            JackMover.DrawMoved();
            try { RoomPressure.Tick(); }
            catch (Exception ex) { Log.LogError("[RoomPressure] " + ex); }
        }

        private static bool sLevelLoading;

        // Start is posted before the scenes load; Complete once the ship is spawned and jointed.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State == LevelLoadEvent.LoadState.Start) { sLevelLoading = true; RoomPressure.OnLevelLoadStart(); return; }
            if (ev.State != LevelLoadEvent.LoadState.Complete && ev.State != LevelLoadEvent.LoadState.Error) return;
            sLevelLoading = false;
            try
            {
                if (ev.State == LevelLoadEvent.LoadState.Complete && Enabled.Value) JackMover.OnShipLoaded();
            }
            catch (Exception ex) { Log.LogError("[JackMover] " + ex); }
            finally { JackMover.RestoreCollisions(); }
        }

        // Posted just before the ship starts spawning into the already-loaded bay (new ship or from a save).
        private static void OnShipSpawnWrapper(ShipAllSpawnWrapperEvent ev)
        {
            if (!sLevelLoading || !Enabled.Value) return;
            if (ev.State != ShipAllSpawnWrapperEvent.SpawnState.Start || ev.Type == ShipAllSpawnWrapperEvent.SpawnType.Despawn) return;
            try { JackMover.OnShipSpawnStarting(); }
            catch (Exception ex) { Log.LogError("[JackMover] " + ex); }
        }
    }
}
