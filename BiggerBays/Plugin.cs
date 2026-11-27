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
    //   BargeStretcher  makes the barge longer and wider to match
    //   BatchedRedraw   draws the station's statically batched meshes where the reshaping wants them
    //   BayLighting     the bay's spotlights: range and intensity, adjustable while the game runs, and scaled to keep a
    //                   wider bay lit
    //   ShipCentering   places each custom ship in the middle of the bay, a set distance from the back wall, and can
    //                   straighten it (taken over from TheExpanse, which no longer does this)
    //   JackMover       with centering on, moves the Master Jack and the five light jacks clear of the ship (same)
    //   OptionsTab      the BAYS page in the game's Options menu
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> Enabled = null!;

        internal static ConfigEntry<bool> BiggerBay = null!;
        internal static ConfigEntry<float> SideClearance = null!;
        internal static ConfigEntry<float> LengthTrigger = null!;

        internal static ConfigEntry<bool> LightsEnabled = null!;
        internal static ConfigEntry<float> LightRange = null!;
        internal static ConfigEntry<float> LightIntensity = null!;
        internal static ConfigEntry<float> HelmetRange = null!;
        internal static ConfigEntry<float> HelmetIntensity = null!;

        internal static ConfigEntry<bool> CenterShip = null!;
        internal static ConfigEntry<bool> StraightenShip = null!;
        internal static ConfigEntry<float> StraightenRatio = null!;
        internal static ConfigEntry<float> BackWallGap = null!;
        internal static ConfigEntry<float> BayCenterY = null!;
        internal static ConfigEntry<float> Clearance = null!;

        private FileSystemWatcher? _watcher;
        private static volatile bool sSettingsFileChanged;
        private static float sSettingsFileChangedAt;
        // Write time of the settings file right after the BAYS page saved it, so that write can be told apart from a
        // hand edit and is not read back in.
        internal static DateTime OwnWriteTimeUtc;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enable or disable BiggerBays.");

            BiggerBay = Config.Bind("Bay", "BiggerBay", true,
                "Make the bay bigger for ships that need it. The ship is measured once it has spawned, and the bay is only changed as " +
                "far as that ship calls for, so a ship that fits leaves the bay exactly as the game has it. Width: both sides of the " +
                "bay move apart, and the back wall and the barge widen with them (see SideClearanceMeters). Length: the bay grows by " +
                "76 m, with a second furnace and processor on each side and a longer barge (see LengthTriggerMeters). Off: the bay " +
                "is never changed.");
            SideClearance = Config.Bind("Bay", "SideClearanceMeters", 20f,
                "The room kept between the ship and each side wall. The sides of the bay move out just far enough to leave this much " +
                "beside the widest part of the ship, up to 40 m each. The stock bay is 79.6 m wide, so at 20 a ship up to 39.6 m " +
                "wide does not widen it.");
            LengthTrigger = Config.Bind("Bay", "LengthTriggerMeters", 25f,
                "How long a ship has to be before the bay is made longer: how far it reaches past the far edge of the stock " +
                "processor. Lower it to lengthen the bay for shorter ships, raise it to keep the stock bay for more of them.");

            LightsEnabled = Config.Bind("Lights", "Enabled", true,
                "Adjust the bay's spotlights with the two multipliers below. When the bay is widened, their range and brightness are " +
                "raised further to make up for the distance. Lights that are part of a ship are never touched. Changes here take " +
                "effect at once.");
            LightRange = Config.Bind("Lights", "RangeMultiplier", 1f, "Multiply bay spotlight range by this factor. 1 = stock.");
            LightIntensity = Config.Bind("Lights", "IntensityMultiplier", 1f, "Multiply bay spotlight brightness by this factor. 1 = stock.");

            HelmetRange = Config.Bind("Lights", "HelmetRangeMultiplier", 1f,
                "Multiply the range of your helmet light by this factor. 1 = stock. This setting is only in this file.");
            HelmetIntensity = Config.Bind("Lights", "HelmetIntensityMultiplier", 1f,
                "Multiply the brightness of your helmet light by this factor. 1 = stock. This setting is only in this file.");

            CenterShip = Config.Bind("Ship", "CenterShip", true,
                "Place custom ships in the middle of the bay and move the jacks clear of them. Large custom ships are posed to spawn " +
                "above the jacks so they can be played without this mod; with this on they are brought down into the bay instead. " +
                "The game's own ships are never moved, and neither are the jacks for them. A ship loaded from a save goes back where " +
                "it was saved.");
            StraightenShip = Config.Bind("Ship", "StraightenShip", true,
                "With CenterShip on: before centering a custom ship, turn it square to the bay. A ship that is already square is not " +
                "touched.");
            StraightenRatio = Config.Bind("Ship", "StraightenRatio", 1.5f,
                "With StraightenShip on: how much longer one side of the ship has to be than the next before its direction is trusted. " +
                "A ship about as wide as it is long has no clear direction and keeps the angle it is posed at. Lower it (down to " +
                "about 1.1) if a ship that is clearly crooked is not being straightened.");
            BackWallGap = Config.Bind("Ship", "BackWallGapMeters", 15f,
                "With CenterShip on: the gap between the back wall and the end of the ship nearest to it.");
            BayCenterY = Config.Bind("Ship", "HeightOffsetMeters", 0f,
                "With CenterShip on: raise (positive) or lower (negative) where ships sit in the bay.");
            Clearance = Config.Bind("Ship", "JackClearanceMeters", 5f,
                "With CenterShip on: the gap kept between the ship and the jacks. The Master Jack is pushed along the bay until it is " +
                "this far past the ship's far end, and the five light jacks are raised together until the lowest is this far above " +
                "the ship's top.");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            ShipCentering.Install(harmony);
            // Per class, so a patch whose target moved in a game update is logged and skipped instead of stopping the rest.
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), inherit: false)) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception ex) { Log.LogError($"Patch {type.Name} failed; that feature is disabled: {ex.GetBaseException().Message}"); }
            }

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
            // Wait for the editor to finish writing before reading the file, and for the BAYS page to be closed so unsaved
            // changes made there are not overwritten.
            if (sSettingsFileChanged)
            {
                sSettingsFileChanged = false;
                sSettingsFileChangedAt = Time.unscaledTime;
            }
            else if (sSettingsFileChangedAt > 0f && Time.unscaledTime - sSettingsFileChangedAt > 0.5f && !EditSession.IsOpen)
            {
                sSettingsFileChangedAt = 0f;
                try
                {
                    // The BAYS page's own save: those values are already in use.
                    if (File.GetLastWriteTimeUtc(Config.ConfigFilePath) != OwnWriteTimeUtc)
                    {
                        Config.Reload();
                        Log.LogInfo("Settings file re-read. Light settings apply now; bay and ship settings apply to the next ship loaded.");
                        BayLighting.OnSettingsChanged();
                    }
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
                ShipCentering.PlacedBox = null;
                // Pick up edits made to the settings file while the game was running, before the next bay is built.
                if (!EditSession.IsOpen)
                {
                    try { Instance.Config.Reload(); }
                    catch (Exception ex) { Log.LogWarning("Could not re-read the settings file; keeping the current settings: " + ex.Message); }
                }
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
            catch (Exception ex) { Log.LogError("[Barge] could not resize the barge: " + ex); }
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
            // Only when BayLights' own bay group is still switched on (read from its settings, nothing of it is changed).
            if (LightsEnabled.Value && others.TryGetValue("me.kerballone.BayLights", out var bayLights) && bayLights.Instance != null
                && bayLights.Instance.Config.TryGetEntry<bool>("BayLights", "Enabled", out var theirs) && theirs.Value)
                Log.LogWarning("BayLights is installed with its [BayLights] Enabled on. Turn that off, or this mod's [Lights] Enabled, so the bay spotlights are not changed twice.");
        }
    }
}
