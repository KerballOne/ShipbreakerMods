using System;
using System.IO;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BiggerBays
{
    // A bigger work bay:
    //   BayReshaper     slides the station's arms apart (width) and its dry docks toward the open end (length), and fills
    //                   the added length with a second furnace and processor on each side
    //   BargeStretcher  makes the barge longer to match
    //   BatchedRedraw   draws the station's statically batched meshes where the reshaping wants them
    //   BayLighting     the bay's spotlights: range, intensity and cone, adjustable while the game runs, and scaled to
    //                   keep the wider bay lit
    //   ShipCentering   places each custom ship in the middle of the bay, a set distance from the back wall, and can
    //                   straighten it (taken over from TheExpanse, which no longer does this)
    //   JackMover       with centering on, moves the Master Jack and the five light jacks clear of the ship (same)
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> Enabled = null!;

        internal static ConfigEntry<bool> BiggerBay = null!;
        internal static ConfigEntry<float> SideClearance = null!;
        internal static ConfigEntry<float> MaxExtraWidth = null!;
        internal static ConfigEntry<float> LengthTrigger = null!;
        internal static ConfigEntry<float> ExtraLength = null!;
        internal static ConfigEntry<bool> SecondSalvageBays = null!;
        internal static ConfigEntry<bool> StraightenSecondBays = null!;
        internal static ConfigEntry<float> SecondBaysTurn = null!;
        internal static ConfigEntry<float> SecondBaysAcross = null!;
        internal static ConfigEntry<bool> MoveMasterJack = null!;
        internal static ConfigEntry<float> ExtraBargeLength = null!;
        internal static ConfigEntry<float> LengthCut = null!;
        internal static ConfigEntry<float> MiddleStrip = null!;

        internal static ConfigEntry<bool> LightsEnabled = null!;
        internal static ConfigEntry<bool> LightKeepCoverage = null!;
        internal static ConfigEntry<float> LightRange = null!;
        internal static ConfigEntry<float> LightIntensity = null!;
        internal static ConfigEntry<float> LightConeAngle = null!;
        internal static ConfigEntry<float> LightMaxAngle = null!;

        internal static ConfigEntry<bool> CenterShip = null!;
        internal static ConfigEntry<float> BayCenterY = null!;
        internal static ConfigEntry<float> AcrossOffset = null!;
        internal static ConfigEntry<float> BackWallGap = null!;
        internal static ConfigEntry<bool> StraightenShip = null!;
        internal static ConfigEntry<float> StraightenRatio = null!;
        internal static ConfigEntry<float> Clearance = null!;
        internal static ConfigEntry<bool> MovePlayerSpawn = null!;

        private FileSystemWatcher? _watcher;
        private static volatile bool sSettingsFileChanged;
        private static float sSettingsFileChangedAt;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enable or disable BiggerBays.");
            BiggerBay = Config.Bind("Bay", "BiggerBay", true,
                "Make the bay and the barge bigger for ships that need it. The ship is measured once it has spawned, and the bay is " +
                "only changed as far as that ship calls for (see SideClearanceMeters and LengthTriggerMeters), so a ship that fits " +
                "the stock bay leaves it exactly as the game has it. Off: the bay and the barge are never changed; the [Lights] and " +
                "[Ship] settings still work.");
            SideClearance = Config.Bind("Bay", "SideClearanceMeters", 20f,
                "The room kept between the ship and each side wall. Each arm of the bay is moved outward just far enough to leave " +
                "this much beside the widest part of the ship. The stock bay is 79.6 m between its side walls, so a ship up to " +
                "39.6 m wide (at the default 20) does not move the arms at all.");
            MaxExtraWidth = Config.Bind("Bay", "MaxExtraWidthMeters", 40f,
                "The furthest each arm is ever moved outward, however wide the ship.");
            LengthTrigger = Config.Bind("Bay", "LengthTriggerMeters", 40f,
                "How far the ship has to reach past the far edge of the stock processor section (see LengthCutPosition) before the " +
                "bay is made longer. The stock Master Jack stands 40 m past that edge, so at the default the bay is lengthened for " +
                "ships that reach beyond where the Master Jack stands.");
            ExtraLength = Config.Bind("Bay", "ExtraLengthMeters", 76f,
                "When the bay is made longer: how far each arm's dry dock (everything from the end of the processor section onward) " +
                "is moved toward the open end. The furnace and processor section is 76 m long, so 76 leaves exactly room for a copy " +
                "of it. 0 never lengthens the bay.");
            SecondSalvageBays = Config.Bind("Bay", "SecondSalvageBays", true,
                "With ExtraLengthMeters above 0: fill the added length on each side with a copy of the furnace and processor section, " +
                "so each side has furnace, processor, furnace, processor from the back wall outward. The copies salvage and scrap " +
                "like the originals. Off: the added length is left open between the processor section and the dry dock.");
            StraightenSecondBays = Config.Bind("Bay", "StraightenSecondBays", true,
                "With SecondSalvageBays on: turn each copied furnace and processor section so its two mouths stand in line with the " +
                "bay. In the stock bay the processor sits 9 m further out than the furnace, about 18 degrees off the bay's axis; a " +
                "plain copy repeats that slant. The section is turned about its processor, so the furnace swings outward into line " +
                "and nothing swings in toward the ship. Off: the copy keeps the stock slant.");
            SecondBaysTurn = Config.Bind("Bay", "SecondBaysTurnDegrees", 0f,
                "With SecondSalvageBays on: an extra turn for each copied section, in degrees, on top of StraightenSecondBays. A " +
                "positive number swings the furnace end further outward, a negative one inward. Both sides mirror each other.");
            SecondBaysAcross = Config.Bind("Bay", "SecondBaysAcrossMeters", 0f,
                "With SecondSalvageBays on: move each copied section this far outward (positive) or toward the middle of the bay " +
                "(negative), on top of ExtraWidthMeters.");
            MoveMasterJack = Config.Bind("Bay", "MoveMasterJackWithLength", false,
                "With ExtraLengthMeters above 0: move the Master Jack, and the player's start point with it, toward the open end by " +
                "the same distance. Off: it stays where the game has it, close to the ship, and the bay simply runs on past it. With " +
                "[Ship] CenterShip on it is still pushed clear of a long custom ship either way.");
            ExtraBargeLength = Config.Bind("Bay", "ExtraBargeLengthMeters", -1f,
                "How much longer the barge is made, along the bay. Its end by the back wall stays where it is and the rest is " +
                "stretched toward the open end; the stock barge is 83.9 m long. -1 uses ExtraLengthMeters, so the barge grows with " +
                "the bay. 0 leaves the barge alone. Applies to the next bay that is loaded.");
            LengthCut = Config.Bind("Bay", "LengthCutPosition", 50f,
                "Advanced. The position along the bay (world Z) where the processor section ends and the dry dock begins. Pieces of an " +
                "arm that start at or past it move with ExtraLengthMeters; pieces before it are copied.");
            MiddleStrip = Config.Bind("Bay", "MiddleStripHalfWidth", 10f,
                "Advanced. Pieces whose middle lies within this distance of the bay's center line stay put when the bay is widened; " +
                "pieces further out move with their side.");

            LightsEnabled = Config.Bind("Lights", "Enabled", true,
                "Adjust the bay's spotlights. Changes to this section take effect as soon as the settings file is saved. If the " +
                "BayLights mod is also installed, turn its [BayLights] Enabled off so the two do not both change the same lights.");
            LightKeepCoverage = Config.Bind("Lights", "KeepCoverage", true,
                "Make up for the wall lights standing further from the middle of a wider bay: their range is raised in proportion to " +
                "the new width and their intensity by its square, on top of the multipliers below.");
            LightRange = Config.Bind("Lights", "RangeMultiplier", 1f, "Multiply bay spotlight range by this factor. 1 = stock.");
            LightIntensity = Config.Bind("Lights", "IntensityMultiplier", 1f, "Multiply bay spotlight intensity by this factor. 1 = stock.");
            LightConeAngle = Config.Bind("Lights", "ConeAngle", -1f, "Override bay spotlight cone angle in degrees (1-179). -1 = leave unchanged.");
            LightMaxAngle = Config.Bind("Lights", "MaxAngle", 60f,
                "Only spotlights whose stock cone angle is at or below this value count as bay spotlights (spotlights with a default " +
                "name count regardless). Lights that are part of a ship are never touched.");

            CenterShip = Config.Bind("Ship", "CenterShip", true,
                "Place custom ships in the middle of the bay and move the jacks to suit. Large custom ships are posed to spawn above the " +
                "jacks so they can be played without this mod; with this on they are brought down into the bay instead. The ship's size " +
                "is worked out before it spawns; it is then centered across the bay and on BayCenterHeight, with its rear end " +
                "BackWallGapMeters in front of the back wall, and the Master Jack and the five light jacks are moved clear of it (see " +
                "[Jacks]). The game's own ships are never moved, and neither are the jacks for them. Off: nothing is moved for any " +
                "ship. A ship restored from a save goes back where it was saved, centered or not.");
            BackWallGap = Config.Bind("Ship", "BackWallGapMeters", 10f,
                "With CenterShip on: the gap between the back wall and the end of the ship nearest to it.");
            AcrossOffset = Config.Bind("Ship", "AcrossOffsetMeters", 0f,
                "With CenterShip on: how far to one side of the bay's middle ships are placed. 0 centers them between the two side " +
                "walls; a positive or negative number moves them towards one wall or the other.");
            BayCenterY = Config.Bind("Ship", "BayCenterHeight", 0f,
                "With CenterShip on: the height ships are centered on. Raise or lower it to sit ships higher or lower in the bay.");
            StraightenShip = Config.Bind("Ship", "StraightenShip", true,
                "With CenterShip on: before centering a custom ship, turn it square to the bay. The ship's own shape is the guide: the " +
                "tightest box that fits around it is found, and the ship is turned by the smallest amount that lines that box up with " +
                "the bay. A ship that is already square is not touched. See StraightenRatio for ships this cannot work on.");
            StraightenRatio = Config.Bind("Ship", "StraightenRatio", 1.5f,
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

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            ShipCentering.Install(harmony);

            Main.EventSystem.AddHandler<LevelLoadEvent>(OnLevelLoad);
            Main.EventSystem.AddHandler<ShipAllSpawnWrapperEvent>(OnShipSpawnWrapper);
            WatchSettingsFile();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        // ---- settings file ------------------------------------------------------------------------------------------
        // Saving the settings file while the game runs re-reads it: the lights change at once, the bay's size on the next
        // bay that is loaded.
        private void WatchSettingsFile()
        {
            try
            {
                string path = Config.ConfigFilePath;
                _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                };
                FileSystemEventHandler changed = (_, __) => sSettingsFileChanged = true;
                _watcher.Changed += changed;
                _watcher.Created += changed;
                _watcher.Renamed += (_, __) => sSettingsFileChanged = true;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.LogWarning("Could not watch the settings file; changes apply when a level loads: " + ex.Message); }
        }

        private void OnDestroy() => _watcher?.Dispose();

        private void Update()
        {
            // Wait for the editor to finish writing before reading the file.
            if (sSettingsFileChanged)
            {
                sSettingsFileChanged = false;
                sSettingsFileChangedAt = Time.unscaledTime;
            }
            else if (sSettingsFileChangedAt > 0f && Time.unscaledTime - sSettingsFileChangedAt > 0.5f)
            {
                sSettingsFileChangedAt = 0f;
                try
                {
                    Config.Reload();
                    Log.LogInfo("Settings file re-read. Light settings apply now; bay size and ship settings apply to the next bay loaded.");
                    BayLighting.OnSettingsChanged();
                }
                catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
            }

            try { BayLighting.Tick(); }
            catch (Exception ex) { Log.LogError("[Lights] " + ex); }
        }

        private void LateUpdate()
        {
            try { BayReshaper.Tick(); BayReshaper.Mirror(); }
            catch (Exception ex) { Log.LogError("[Bay] " + ex); }
            try { BargeStretcher.Tick(); }
            catch (Exception ex) { Log.LogError("[Barge] " + ex); }
            BatchedRedraw.Draw();
        }

        // ---- level loading ------------------------------------------------------------------------------------------
        private static bool sLevelLoading;
        private static bool sWarnedAboutOtherMods;

        // Start is posted before the scenes load; Complete once the ship is spawned and jointed.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State == LevelLoadEvent.LoadState.Start)
            {
                ShipCentering.Centered = false;
                // Pick up edits made to the settings file while the game was running, before the next bay is built.
                try { Instance.Config.Reload(); }
                catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
                sLevelLoading = true;
                BayReshaper.OnLevelLoadStart();
                BargeStretcher.OnLevelLoadStart();
                BayMemory.OnLevelLoadStart();
                JackMover.OnLevelLoadStart();
                BayLighting.OnLevelLoadStart();
                WarnAboutOtherMods();
                return;
            }
            if (ev.State != LevelLoadEvent.LoadState.Complete && ev.State != LevelLoadEvent.LoadState.Error) return;
            sLevelLoading = false;
            try
            {
                if (ev.State == LevelLoadEvent.LoadState.Complete && Enabled.Value)
                {
                    Reshape();          // sized to the ship, which is now in place
                    if (CenterShip.Value && ShipCentering.Centered) JackMover.OnShipLoaded();
                }
            }
            catch (Exception ex) { Log.LogError("[JackMover] " + ex); }
            finally { JackMover.RestoreCollisions(); }

            try { BayLighting.OnLevelLoaded(); }
            catch (Exception ex) { Log.LogError("[Lights] " + ex); }
        }

        // Posted just before the ship starts spawning into the already-loaded bay (new ship or from a save).
        private static void OnShipSpawnWrapper(ShipAllSpawnWrapperEvent ev)
        {
            if (!sLevelLoading || !Enabled.Value || !CenterShip.Value) return;
            if (ev.State != ShipAllSpawnWrapperEvent.SpawnState.Start || ev.Type == ShipAllSpawnWrapperEvent.SpawnType.Despawn) return;
            try { JackMover.OnShipSpawnStarting(); }
            catch (Exception ex) { Log.LogError("[JackMover] " + ex); }
        }

        // Does nothing when the bay loaded now has already been reshaped, when there is no bay, or when the ship fits.
        private static void Reshape()
        {
            try { BayReshaper.Apply(); }
            catch (Exception ex) { Log.LogError("[Bay] could not reshape the bay: " + ex); }
            try { BargeStretcher.Apply(); }
            catch (Exception ex) { Log.LogError("[Barge] could not lengthen the barge: " + ex); }
        }

        private static void WarnAboutOtherMods()
        {
            if (sWarnedAboutOtherMods) return;
            sWarnedAboutOtherMods = true;
            var others = BepInEx.Bootstrap.Chainloader.PluginInfos;
            // TheExpanse centered ships and moved the jacks itself up to version 0.5.0.
            if (others.TryGetValue("me.kerballone.TheExpanse", out var expanse) && expanse.Metadata.Version < new Version(0, 6, 0) && CenterShip.Value)
                Log.LogWarning($"TheExpanse {expanse.Metadata.Version} is installed and still centers ships and moves the jacks itself. Update it to 0.6.0 or later, " +
                               "or turn off its [Bay] CenterShip, so ships are not moved twice.");
            if (others.ContainsKey("me.kerballone.BayLights") && LightsEnabled.Value)
                Log.LogWarning("BayLights is installed. Turn off its [BayLights] Enabled, or this mod's [Lights] Enabled, so the bay spotlights are not changed twice.");
        }
    }
}
