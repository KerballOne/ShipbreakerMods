using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BBI.Unity.Game;

namespace BiggerBays
{
    // Remembers how big the bay was made for each ship, so that loading a save gives the same bay again.
    //
    // The bay is sized from the ship as it stands when the level has loaded. For a new ship that is the whole ship where
    // it was placed. For a ship loaded from a save it would be whatever is left of it, wherever its loose parts have
    // drifted, which could give a different bay each time. So the sizes worked out for a new ship are written down, and a
    // ship loaded from a save gets the sizes written down for it.
    //
    // The record is a text file in BepInEx\config, one line per profile and ship: profile|ship|extra width|extra length.
    // A profile has one ship in progress at a time, so a new ship of the same name simply replaces the line. The game's
    // own save files are never touched.
    internal static class BayMemory
    {
        static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, PluginInfo.PLUGIN_GUID + ".ships.txt");

        static string? sShip;
        static bool sRestoring;

        internal static void OnLevelLoadStart()
        {
            sShip = null;
            sRestoring = false;
        }

        // Called as a ship starts to spawn.
        internal static void OnShipSpawning(string? shipName, bool fromSave)
        {
            sShip = shipName;
            sRestoring = fromSave;
        }

        static string? Key()
        {
            if (string.IsNullOrEmpty(sShip)) return null;
            string profile = "";
            try { profile = PlayerProfileService.Instance?.Profile?.ProfileName ?? ""; }
            catch { /* no profile (free play, tests): keyed by ship alone */ }
            return Clean(profile) + "|" + Clean(sShip!);
        }

        static string Clean(string s) => s.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();

        static Dictionary<string, string> Load()
        {
            var lines = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(FilePath)) return lines;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var f = line.Split('|');
                if (f.Length == 4) lines[f[0] + "|" + f[1]] = f[2] + "|" + f[3];
            }
            return lines;
        }

        // For a ship being loaded from a save: the sizes recorded when it was new, if there are any.
        internal static bool TryRecall(out float width, out float length)
        {
            width = length = 0f;
            if (!sRestoring) return false;
            try
            {
                string? key = Key();
                if (key == null || !Load().TryGetValue(key, out var value)) return false;
                var f = value.Split('|');
                return float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out width)
                    && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out length);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Bay] could not read the record of bay sizes; the ship is measured instead: " + ex.Message);
                return false;
            }
        }

        internal static void Remember(float width, float length)
        {
            try
            {
                string? key = Key();
                if (key == null) return;
                var lines = Load();
                lines[key] = width.ToString("0.###", CultureInfo.InvariantCulture) + "|" + length.ToString("0.###", CultureInfo.InvariantCulture);
                var text = new List<string>();
                foreach (var kv in lines) text.Add(kv.Key + "|" + kv.Value);
                text.Sort(StringComparer.Ordinal);
                File.WriteAllLines(FilePath, text.ToArray());
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Bay] could not record this ship's bay sizes; a reload will measure the ship again: " + ex.Message); }
        }

        internal static bool Restoring => sRestoring;
    }
}
