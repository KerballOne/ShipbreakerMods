using System;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HudReposition
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        internal static ConfigEntry<string> PartInfoOffset     = null!;
        internal static ConfigEntry<string> SalvageLabelOffset = null!;

        internal static Vector2 ParseOffset(ConfigEntry<string> entry)
        {
            try
            {
                var s = entry.Value.Trim().Trim('(', ')');
                var parts = s.Split(',');
                if (parts.Length != 2) throw new FormatException();
                float x = float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                float y = float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                return new Vector2(x, y);
            }
            catch
            {
                Log.LogWarning($"[HudReposition] Could not parse '{entry.Value}' — expected format: (x, y). Using (0, 0).");
                return Vector2.zero;
            }
        }

        private void Awake()
        {
            Log = Logger;

            const string partDesc = "Offset as percentage of screen size. Format: (x, y). " +
                                   "e.g. (-15, 10) moves 15% left and 10% up. Negative y = down. " +
                                   "Moves both the part info panel and the salvage label together.";

            const string salvageDesc = "OPTIONAL. Additional offset for the salvage destination label (PROCESSOR/BARGE/FURNACE) " +
                                       "on top of the PartInfoPanel offset. Format: (x, y). " +
                                       "Use this only if you want the salvage label in a different position relative to the part info panel. " +
                                       "Leave at (0, 0) to keep it grouped with the part info panel.";

            PartInfoOffset     = Config.Bind("PartInfoPanel", "Offset", "(0, -8)", partDesc);
            SalvageLabelOffset = Config.Bind("SalvageLabel",  "Offset (Optional)", "(0, 0)", salvageDesc);

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            Log.LogInfo("HudReposition loaded.");
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID    = "me.kerballone.HudReposition";
        public const string PLUGIN_NAME    = "HudReposition";
        public const string PLUGIN_VERSION = "1.0.0";
    }
}
