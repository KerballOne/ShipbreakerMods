using System;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace TheExpanse
{
    // Makes room in the work bay for ships larger than the bay was laid out for.
    // Step one: measure the ship when a level finishes loading and, if it is oversized, move the Master Jack and the
    // five light jacks out of its way (see JackMover).
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> Enabled = null!;
        internal static ConfigEntry<bool> DebugLogging = null!;
        internal static ConfigEntry<float> ThresholdX = null!;
        internal static ConfigEntry<float> ThresholdY = null!;
        internal static ConfigEntry<float> ThresholdZ = null!;
        internal static ConfigEntry<float> Clearance = null!;
        internal static ConfigEntry<bool> MovePlayerSpawn = null!;
        internal static ConfigEntry<string> VariantWeights = null!;
        internal static ConfigEntry<bool> ExportPlanets = null!;
        internal static ConfigEntry<bool> MarsEnabled = null!;
        internal static ConfigEntry<string> MarsImage = null!;
        internal static ConfigEntry<UnityEngine.Color> MarsGlow = null!;
        internal static ConfigEntry<bool> MarsHideMoon = null!;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enable or disable TheExpanse.");
            DebugLogging = Config.Bind("General", "DebugLogging", true,
                "Log the ship's bounding box per root object, every jack's before/after position and a follow-up check that the moves held.");

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

            VariantWeights = Config.Bind("Variants", "Weights", "d51a0f78d6974d7881b6d7214326fed2=1",
                "Ship variants that only spawn with this mod installed. Each is shipped with Weight 0 in its ModuleEntryDefinition, so the " +
                "game never picks it; this mod gives it the weight listed here. Format: comma-separated guid=weight, where guid is the " +
                "GUID of the variant's module prefab (the entry's ModuleDefRef). Weights are relative to the other entries in the same " +
                "list (a base ship at weight 1 and a variant at 1 is a 50/50 pick). Default entry: Tachi_Gas.");

            MarsEnabled = Config.Bind("Mars", "Enabled", true,
                "Replace the background Earth with the image named below. Applied after each level load; if the image file is missing the Earth is left as it is.");
            MarsImage = Config.Bind("Mars", "ImageFile", "Mars.png",
                "Replacement planet image, in the same folder as this plugin's DLL. A square PNG with transparency laid out like the game's " +
                "own Earth image (see the PlanetExport folder): 4096 x 4096, the disc centred and about 3200 px across, lit from the upper " +
                "left with a solid black night side at the lower right, soft transparent edge. Edits are picked up on the next level load.");
            MarsGlow = Config.Bind("Mars", "GlowColor", new UnityEngine.Color(0.85f, 0.5f, 0.3f, 0.13f),
                "Colour of the atmosphere glow around the planet, as RRGGBBAA; the last pair is its strength. Stock Earth is 1EB1FF63 (sky blue at 39%).");
            MarsHideMoon = Config.Bind("Mars", "HideMoon", true, "Hide the Moon while the planet is replaced.");

            ExportPlanets = Config.Bind("Export", "ExportPlanetTextures", false,
                "When set to true: on the next level load, write the background planet cards' textures (PNG), meshes (OBJ) and a report of their materials " +
                "to the PlanetExport folder beside this plugin's DLL, as a template for replacement art. Switches itself off after it has run.");

            VariantUnlock.Install(new Harmony(PluginInfo.PLUGIN_GUID));

            Main.EventSystem.AddHandler<LevelLoadEvent>(OnLevelLoad);
            Main.EventSystem.AddHandler<ShipAllSpawnWrapperEvent>(OnShipSpawnWrapper);
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        private void LateUpdate() => JackMover.DrawMoved();

        private static bool sLevelLoading;

        // Start is posted before the scenes load; Complete once the ship is spawned and jointed.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State == LevelLoadEvent.LoadState.Start) { sLevelLoading = true; return; }
            if (ev.State != LevelLoadEvent.LoadState.Complete && ev.State != LevelLoadEvent.LoadState.Error) return;
            sLevelLoading = false;
            if (ev.State == LevelLoadEvent.LoadState.Complete && ExportPlanets.Value)
            {
                try
                {
                    string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Instance.Info.Location)!, "PlanetExport");
                    if (PlanetExport.Run(dir)) ExportPlanets.Value = false;      // one-off: switches itself off once it has run
                    else Log.LogInfo("[PlanetExport] no planet cards in this level yet; will try again on the next load.");
                }
                catch (Exception ex) { Log.LogError("[PlanetExport] " + ex); }
            }
            if (ev.State == LevelLoadEvent.LoadState.Complete)
            {
                try { PlanetSwap.Apply(); }
                catch (Exception ex) { Log.LogError("[PlanetSwap] " + ex); }
            }
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
