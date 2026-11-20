using System;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Themes
{
    // Reskins the game from folders of the user's own pictures, beside the DLL:
    //   Menu\       MenuBackdrop      the main menu's backdrop, one picked at random per game launch
    //   Loading\    LoadingPictures   a picture on the loading screen, one picked at random per load
    //   Planet\     PlanetSwap        the background planet, one picked at random per game launch
    // An empty or missing folder leaves that part of the game exactly as it ships.
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance = null!;
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<bool> MenuEnabled = null!;
        internal static ConfigEntry<string> MenuFolder = null!;
        internal static ConfigEntry<bool> LoadingEnabled = null!;
        internal static ConfigEntry<string> LoadingFolder = null!;
        internal static ConfigEntry<float> LoadingZoom = null!;
        internal static ConfigEntry<bool> LoadingStretch = null!;
        internal static ConfigEntry<bool> LoadingBlackBackground = null!;
        internal static ConfigEntry<bool> LoadingHideLogo = null!;
        internal static ConfigEntry<bool> PlanetEnabled = null!;
        internal static ConfigEntry<string> PlanetFolder = null!;
        internal static ConfigEntry<UnityEngine.Color> PlanetGlow = null!;
        internal static ConfigEntry<bool> PlanetHideMoon = null!;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MenuEnabled = Config.Bind("Menu", "Enabled", true,
                "Replace the main menu's backdrop picture with one picked at random, each time the game is started, from the folder below. " +
                "With no pictures in the folder the menu is left as it is.");
            MenuFolder = Config.Bind("Menu", "Folder", "Menu",
                "Folder holding the backdrop pictures (PNG or JPG), beside this plugin's DLL. The game's own picture is 2048 x 1159; " +
                "a picture of the same shape is shown exactly where the original was.");

            LoadingEnabled = Config.Bind("Loading", "Enabled", true,
                "Show a picture on the loading screen: one PNG or JPG picked at random per load from the folder below, shown whole and " +
                "centred (see Zoom). With no pictures in the folder the loading screen is left as it is.");
            LoadingFolder = Config.Bind("Loading", "Folder", "Loading",
                "Folder holding the pictures, beside this plugin's DLL. The tooltip, loading bar and prompt cover the bottom third of the " +
                "screen, so keep the subject in the upper two thirds.");
            LoadingZoom = Config.Bind("Loading", "Zoom", 0f,
                new ConfigDescription("How far a picture is enlarged when its shape differs from the screen's. 0 shows the whole picture, with " +
                "the background beside it; 1 enlarges it until the screen is covered, cropping what overflows.",
                new AcceptableValueRange<float>(0f, 1f)));
            LoadingStretch = Config.Bind("Loading", "Stretch", false,
                "Pull each picture to the screen's shape so it covers the whole screen with nothing cropped; pictures of a different shape " +
                "are distorted. When on, Zoom is ignored.");
            LoadingBlackBackground = Config.Bind("Loading", "BlackBackground", true,
                "Put plain black behind the picture, so the loading screen's teal background does not show beside it.");
            LoadingHideLogo = Config.Bind("Loading", "HideLogo", true,
                "Hide the LYNX logo panel in the middle of the loading screen while a picture is showing.");

            PlanetEnabled = Config.Bind("Planet", "Enabled", true,
                "Replace the background planet (the Earth) with a picture picked at random, each time the game is started, from the folder " +
                "below. With no pictures in the folder the planet, its glow and the Moon are left as they are.");
            PlanetFolder = Config.Bind("Planet", "Folder", "Planet",
                "Folder holding the planet pictures, beside this plugin's DLL. Each is a square PNG with transparency laid out like the " +
                "game's own Earth image: 4096 x 4096, the disc centred and about 3200 px across, lit from the upper left with a solid " +
                "black night side at the lower right, soft transparent edge.");
            PlanetGlow = Config.Bind("Planet", "GlowColor", new UnityEngine.Color(0.85f, 0.5f, 0.3f, 0.13f),
                "Colour of the atmosphere glow around a replaced planet, as RRGGBBAA; the last pair is its strength. Stock Earth is " +
                "1EB1FF63 (sky blue at 39%).");
            PlanetHideMoon = Config.Bind("Planet", "HideMoon", true, "Hide the Moon while the planet is replaced.");

            Main.EventSystem.AddHandler<LevelLoadEvent>(OnLevelLoad);
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION} loaded.");
        }

        private void Update()
        {
            try { LoadingPictures.Tick(); }
            catch (Exception ex) { Log.LogError("[Loading] " + ex); }

            try { MenuBackdrop.Tick(); }
            catch (Exception ex) { Log.LogError("[Menu] " + ex); }
        }

        // The planet cards belong to the level, so the swap is applied again each time one has loaded.
        private static void OnLevelLoad(LevelLoadEvent ev)
        {
            if (ev.State != LevelLoadEvent.LoadState.Complete) return;
            try { PlanetSwap.Apply(); }
            catch (Exception ex) { Log.LogError("[Planet] " + ex); }
        }
    }
}
