using System;
using System.Collections.Generic;
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
        internal static Plugin Instance = null!;

        internal static ConfigEntry<float> PartInfoOffsetX     = null!;
        internal static ConfigEntry<float> PartInfoOffsetY     = null!;
        internal static ConfigEntry<float> SalvageLabelOffsetX = null!;
        internal static ConfigEntry<float> SalvageLabelOffsetY = null!;

        internal static Vector2 PartInfoOffset     => new Vector2(PartInfoOffsetX.Value, PartInfoOffsetY.Value);
        internal static Vector2 SalvageLabelOffset => new Vector2(SalvageLabelOffsetX.Value, SalvageLabelOffsetY.Value);

        // Parses the old "(x, y)" string format.
        private static bool TryParseOffset(string text, out Vector2 offset)
        {
            offset = Vector2.zero;
            var parts = text.Trim().Trim('"').Trim().Trim('(', ')').Split(',');
            if (parts.Length != 2) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            offset = new Vector2(x, y);
            return true;
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            var range = new AcceptableValueRange<float>(-50f, 50f);
            const string partDesc = "Offset as a percentage of the screen size. Negative X = left, negative Y = down. " +
                                    "Moves both the part info panel and the salvage label together.";
            const string salvageDesc = "OPTIONAL. Additional offset for the salvage destination label (PROCESSOR/BARGE/FURNACE) " +
                                       "on top of the PartInfoPanel offset, as a percentage of the screen size. " +
                                       "Leave at 0 to keep it grouped with the part info panel.";

            PartInfoOffsetX     = Config.Bind("PartInfoPanel", "OffsetX", 0f,  new ConfigDescription("Horizontal. " + partDesc, range));
            PartInfoOffsetY     = Config.Bind("PartInfoPanel", "OffsetY", -8f, new ConfigDescription("Vertical. " + partDesc, range));
            SalvageLabelOffsetX = Config.Bind("SalvageLabel",  "OffsetX", 0f,  new ConfigDescription("Horizontal. " + salvageDesc, range));
            SalvageLabelOffsetY = Config.Bind("SalvageLabel",  "OffsetY", 0f,  new ConfigDescription("Vertical. " + salvageDesc, range));
            MigrateOffsetStrings();

            // Moving a slider on the HUD options tab re-positions the panels right away.
            foreach (var entry in new[] { PartInfoOffsetX, PartInfoOffsetY, SalvageLabelOffsetX, SalvageLabelOffsetY })
                entry.SettingChanged += (_, _) => HudOffset.ApplyAll();

            // Per class rather than PatchAll(), so a patch whose target moved in a game update is
            // logged and skipped instead of aborting every patch after it.
            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), inherit: false)) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception ex) { Log.LogError($"Patch {type.Name} failed; that feature is disabled: {ex.GetBaseException().Message}"); }
            }
            Log.LogInfo("HudReposition loaded.");
        }

        // 1.0.0 stored each offset as one "(x, y)" string. Carry those over to the X/Y settings and
        // drop the old keys from the file.
        private void MigrateOffsetStrings()
        {
            var orphans = Traverse.Create(Config).Property("OrphanedEntries").GetValue<Dictionary<ConfigDefinition, string>>();
            if (orphans == null || orphans.Count == 0) return;
            var moved = 0;
            foreach (var (section, key, x, y) in new[]
            {
                ("PartInfoPanel", "Offset", PartInfoOffsetX, PartInfoOffsetY),
                ("SalvageLabel", "Offset (Optional)", SalvageLabelOffsetX, SalvageLabelOffsetY),
            })
            {
                var definition = new ConfigDefinition(section, key);
                if (!orphans.TryGetValue(definition, out var text)) continue;
                orphans.Remove(definition);
                if (TryParseOffset(text, out var offset))
                {
                    x.Value = offset.x;
                    y.Value = offset.y;
                    moved++;
                }
                else Log.LogWarning($"[HudReposition] Could not parse old {section} offset '{text}'; using the default.");
            }
            Config.Save();
            if (moved > 0) Log.LogInfo($"[HudReposition] Carried {moved} offset setting(s) over to the new X/Y settings.");
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID    = "me.kerballone.HudReposition";
        public const string PLUGIN_NAME    = "HudReposition";
        public const string PLUGIN_VERSION = "1.0.1";
    }
}
