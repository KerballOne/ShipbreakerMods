using System;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace TheExpanse
{
    // Changes to the work bay and to ships for large custom ships:
    //   ShipCentering   places each custom ship in the middle of the bay, a set distance from the back wall (and can straighten it)
    //   JackMover       with centering on, moves the Master Jack and the five light jacks clear of the ship
    //   RoomPressure    makes each room's own pressurization / decompression time count, so big rooms take longer
    //   PartSeparation  on custom ships, heavy parts that overlap their neighbors come free gently instead of being thrown
    // (The Mars planet, loading screen and menu pictures that started here now live in the Themes mod.)
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> Enabled = null!;
        internal static ConfigEntry<bool> CenterShip = null!;
        internal static ConfigEntry<float> BayCenterY = null!;
        internal static ConfigEntry<float> AcrossOffset = null!;
        internal static ConfigEntry<float> BackWallGap = null!;
        internal static ConfigEntry<bool> StraightenShip = null!;
        internal static ConfigEntry<float> StraightenRatio = null!;
        internal static ConfigEntry<float> Clearance = null!;
        internal static ConfigEntry<bool> MovePlayerSpawn = null!;
        internal static ConfigEntry<bool> RoomPressureTimes = null!;
        internal static ConfigEntry<bool> GentleSeparation = null!;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enable or disable TheExpanse.");

            CenterShip = Config.Bind("Bay", "CenterShip", true,
                "Place custom ships in the middle of the bay and move the jacks to suit. Large custom ships are posed to spawn above the " +
                "jacks so they can be played without this mod; with this on they are brought down into the bay instead. The ship's size " +
                "is worked out before it spawns; it is then centered across the bay and on BayCenterHeight, with its rear end " +
                "BackWallGapMeters in front of the back wall, and the Master Jack and the five light jacks are moved clear of it (see " +
                "[Jacks]). The game's own ships are never moved, and neither are the jacks for them. Off: nothing is moved for any " +
                "ship. A ship restored from a save goes back where it was saved, centered or not.");
            BackWallGap = Config.Bind("Bay", "BackWallGapMeters", 10f,
                "With CenterShip on: the gap between the back wall and the end of the ship nearest to it.");
            AcrossOffset = Config.Bind("Bay", "AcrossOffsetMeters", 0f,
                "With CenterShip on: how far to one side of the bay's middle ships are placed. 0 centers them between the two side " +
                "walls; a positive or negative number moves them towards one wall or the other.");
            BayCenterY = Config.Bind("Bay", "BayCenterHeight", 0f,
                "With CenterShip on: the height ships are centered on. Raise or lower it to sit ships higher or lower in the bay.");
            StraightenShip = Config.Bind("Bay", "StraightenShip", true,
                "With CenterShip on: before centering a custom ship, turn it square to the bay. The ship's own shape is the guide: the " +
                "tightest box that fits around it is found, and the ship is turned by the smallest amount that lines that box up with " +
                "the bay. A ship that is already square is not touched. See StraightenRatio for ships this cannot work on.");
            StraightenRatio = Config.Bind("Bay", "StraightenRatio", 1.5f,
                "With StraightenShip on: how much longer one side of the ship has to be than the next before its direction is trusted. " +
                "A ship about as wide as it is long has no clear direction and keeps the angle it is posed at. If only its length (or " +
                "only its thickness) stands out, that one axis is lined up, and the roll about it is taken from the ship's mirror symmetry " +
                "(its left and right halves) where it has one; otherwise the ship is not rolled.");

            Clearance = Config.Bind("Jacks", "ClearanceMeters", 5f,
                "With CenterShip on: the gap kept between the ship and the jacks. The Master Jack is pushed along the bay until it is this " +
                "far past the ship's far end; the five light jacks are raised together until the lowest is this far above the ship's top. " +
                "A jack that already has this much room is left where it is, and so is one the whole ship is posed above.");
            MovePlayerSpawn = Config.Bind("Jacks", "MovePlayerSpawnWithMasterJack", true,
                "Shift the player's start / respawn point by the same amount as the Master Jack, so shifts still start at its door.");

            RoomPressureTimes = Config.Bind("Rooms", "UsePressureTimes", true,
                "Make each room's own Pressurization Time and Decompression Time count. The game ignores them and changes every room's " +
                "pressure in the same few seconds; with this on, a room whose time is set longer than the game's takes that long. Connected " +
                "rooms change together and take the longest time among them. Rooms at or below the game's own time, and hull breaches, " +
                "behave as stock.");

            GentleSeparation = Config.Bind("Parts", "GentleSeparation", true,
                "On custom ships, stop heavy parts being thrown when they are cut or yanked free. Parts collide as the convex shape " +
                "around their mesh, so on many custom ships neighboring parts overlap, and the game pushes overlapping parts apart at " +
                "up to 10 m/s the moment they separate. With this on, that push is limited by mass: parts of 5 tonnes or more are " +
                "eased apart at 0.3 m/s, parts up to 100 kg keep the game's speed, and parts in between fall on a smooth slide. The " +
                "grapple, tethers, explosions and decompression are not changed. The game's own ships are left alone.");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            ShipCentering.Install(harmony);
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
            try { PartSeparation.Tick(); }
            catch (Exception ex) { Log.LogError("[Parts] " + ex); }
        }

        private static bool sLevelLoading;

        // Start is posted before the scenes load; Complete once the ship is spawned and jointed.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State == LevelLoadEvent.LoadState.Start)
            {
                ShipCentering.Centered = false;
                PartSeparation.CustomShip = false;
                // Pick up edits made to the settings file while the game was running, before the next ship is placed.
                try { Instance.Config.Reload(); }
                catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
                sLevelLoading = true;
                RoomPressure.OnLevelLoadStart();
                return;
            }
            if (ev.State != LevelLoadEvent.LoadState.Complete && ev.State != LevelLoadEvent.LoadState.Error) return;
            sLevelLoading = false;
            try
            {
                if (ev.State == LevelLoadEvent.LoadState.Complete && Enabled.Value && CenterShip.Value && ShipCentering.Centered) JackMover.OnShipLoaded();
            }
            catch (Exception ex) { Log.LogError("[JackMover] " + ex); }
            finally { JackMover.RestoreCollisions(); }
        }

        // Posted just before the ship starts spawning into the already-loaded bay (new ship or from a save).
        private static void OnShipSpawnWrapper(ShipAllSpawnWrapperEvent ev)
        {
            if (!sLevelLoading || !Enabled.Value || !CenterShip.Value) return;
            if (ev.State != ShipAllSpawnWrapperEvent.SpawnState.Start || ev.Type == ShipAllSpawnWrapperEvent.SpawnType.Despawn) return;
            try { JackMover.OnShipSpawnStarting(); }
            catch (Exception ex) { Log.LogError("[JackMover] " + ex); }
        }
    }
}
