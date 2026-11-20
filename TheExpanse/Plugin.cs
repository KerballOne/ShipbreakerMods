using System;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace TheExpanse
{
    // A Mars-side salvage yard for oversized ships:
    //   JackMover          measures the ship after each level load and moves the Master Jack and the five light jacks clear of it
    //   VariantUnlock      gives weight-0 ship variants a spawn weight, so they only appear with this mod installed
    //   PlanetSwap         replaces the background Earth with Mars
    //   LoadScreenImages   shows a picture on the loading screen
    //   TitleScreen        replaces the main menu's backdrop picture
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
        internal static ConfigEntry<bool> TitleEnabled = null!;
        internal static ConfigEntry<string> TitleImage = null!;
        internal static ConfigEntry<bool> LoadScreenEnabled = null!;
        internal static ConfigEntry<string> LoadScreenFolder = null!;
        internal static ConfigEntry<bool> LoadScreenHideLogo = null!;
        internal static ConfigEntry<float> LoadScreenZoom = null!;
        internal static ConfigEntry<bool> LoadScreenStretch = null!;
        internal static ConfigEntry<bool> LoadScreenBlackBackground = null!;
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
                "own Earth image: 4096 x 4096, the disc centred and about 3200 px across, lit from the upper " +
                "left with a solid black night side at the lower right, soft transparent edge. Edits are picked up on the next level load.");
            MarsGlow = Config.Bind("Mars", "GlowColor", new UnityEngine.Color(0.85f, 0.5f, 0.3f, 0.13f),
                "Colour of the atmosphere glow around the planet, as RRGGBBAA; the last pair is its strength. Stock Earth is 1EB1FF63 (sky blue at 39%).");
            MarsHideMoon = Config.Bind("Mars", "HideMoon", true, "Hide the Moon while the planet is replaced.");

            LoadScreenEnabled = Config.Bind("LoadScreen", "Enabled", true,
                "Show a picture on the loading screen: one PNG or JPG picked at random per load from the folder below, shown whole and " +
                "centred (see Zoom). With no pictures in the folder the loading screen is left as it is.");
            LoadScreenFolder = Config.Bind("LoadScreen", "Folder", "LoadScreens",
                "Folder holding the pictures, beside this plugin's DLL. The tooltip, loading bar and prompt cover the bottom third of the " +
                "screen, so keep the subject in the upper two thirds.");
            LoadScreenZoom = Config.Bind("LoadScreen", "Zoom", 0f,
                new ConfigDescription("How far a picture is enlarged when its shape differs from the screen's. 0 shows the whole picture, with " +
                "the loading screen's own background beside it; 1 enlarges it until the screen is covered, cropping what overflows.",
                new AcceptableValueRange<float>(0f, 1f)));
            LoadScreenStretch = Config.Bind("LoadScreen", "Stretch", false,
                "Pull each picture to the screen's shape so it covers the whole screen with nothing cropped; pictures of a different shape " +
                "are distorted. When on, Zoom is ignored.");
            LoadScreenBlackBackground = Config.Bind("LoadScreen", "BlackBackground", true,
                "Put plain black behind the picture, so the loading screen's teal background does not show beside it.");
            LoadScreenHideLogo = Config.Bind("LoadScreen", "HideLogo", true,
                "Hide the LYNX logo panel in the middle of the loading screen while a picture is showing.");

            TitleEnabled = Config.Bind("TitleScreen", "Enabled", true,
                "Replace the main menu's backdrop picture with the image named below. If the file is missing the menu is left as it is.");
            TitleImage = Config.Bind("TitleScreen", "ImageFile", "TitleScreen.png",
                "Replacement backdrop, in the same folder as this plugin's DLL. The game's own picture is 2048 x 1159; an image of the " +
                "same shape is shown exactly where the original was.");

            VariantUnlock.Install(new Harmony(PluginInfo.PLUGIN_GUID));

            Main.EventSystem.AddHandler<LevelLoadEvent>(OnLevelLoad);
            Main.EventSystem.AddHandler<ShipAllSpawnWrapperEvent>(OnShipSpawnWrapper);
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        private void Update()
        {
            try { LoadScreenImages.Tick(); }
            catch (Exception ex) { Log.LogError("[LoadScreenImages] " + ex); }

            try { TitleScreen.Tick(); }
            catch (Exception ex) { Log.LogError("[TitleScreen] " + ex); }
        }

        private void LateUpdate() => JackMover.DrawMoved();

        private static bool sLevelLoading;

        // Start is posted before the scenes load; Complete once the ship is spawned and jointed.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State == LevelLoadEvent.LoadState.Start) { sLevelLoading = true; return; }
            if (ev.State != LevelLoadEvent.LoadState.Complete && ev.State != LevelLoadEvent.LoadState.Error) return;
            sLevelLoading = false;
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
