using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using UnityEngine;

namespace PartInfoLogger
{
    // Custom-ship prefabs give every loader-filled StructurePart a stand-in StructurePartAsset named
    // "SPA_Placeholder", so the job board's ship summary never meets an empty part. ModdedShipLoader is
    // meant to overwrite every one of them with the real asset before the ship is playable. This checks
    // that it did: after gameplay starts it lists every StructurePart that still holds the placeholder,
    // or holds no asset at all.
    //
    // Output: placeholder_check.txt next to OutputPath, and a one-line summary in the log. Read-only.
    internal static class PlaceholderCheck
    {
        internal const string PlaceholderName = "SPA_Placeholder";

        static string PathOf(Transform t)
        {
            var p = t.name;
            for (var c = t.parent; c != null; c = c.parent) p = c.name + "/" + p;
            return p;
        }

        internal static IEnumerator Run(float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);

            var parts = Object.FindObjectsOfType<StructurePart>();
            var stuck = new List<string>();
            var empty = new List<string>();
            // A part that was set up while it still held the placeholder keeps what it derived from it even
            // after the real asset arrives: no scanner info (cached in StructurePart.Start) and no highlight
            // component (added once when the part's entity is initialised).
            var staleScanner = new List<string>();
            var staleHighlight = new List<string>();
            foreach (var sp in parts)
            {
                var asset = sp.StructurePartAsset;
                if (asset == null) { empty.Add(PathOf(sp.transform)); continue; }
                if (asset.name == PlaceholderName) { stuck.Add(PathOf(sp.transform)); continue; }
                if (asset.Data.ScannerComponentAsset != null && sp.ScannerComponentAsset == null && sp.gameObject.activeInHierarchy)
                    staleScanner.Add(PathOf(sp.transform));
                if (asset.Data.HighlightAsset != null && sp.Highlight == null && sp.gameObject.activeInHierarchy)
                    staleHighlight.Add(PathOf(sp.transform));
            }

            var verdict = stuck.Count == 0 && staleScanner.Count == 0 && staleHighlight.Count == 0 ? "OK" : "ATTENTION";
            var summary = $"[PlaceholderCheck] {verdict}: {parts.Length} StructureParts, " +
                          $"{stuck.Count} still on {PlaceholderName}, {empty.Count} with no asset, " +
                          $"{staleScanner.Count} with no scanner info, {staleHighlight.Count} with no highlight";
            if (verdict == "OK") Plugin.Log.LogInfo(summary); else Plugin.Log.LogWarning(summary);

            try
            {
                var lines = new List<string> { summary, "" };
                lines.Add($"Still on {PlaceholderName} ({stuck.Count}):");
                lines.AddRange(stuck.OrderBy(s => s).Select(s => "  " + s));
                lines.Add("");
                lines.Add($"No StructurePartAsset ({empty.Count}):");
                lines.AddRange(empty.OrderBy(s => s).Select(s => "  " + s));
                lines.Add("");
                lines.Add($"Real asset has scanner info but the part has none ({staleScanner.Count}):");
                lines.AddRange(staleScanner.OrderBy(s => s).Select(s => "  " + s));
                lines.Add("");
                lines.Add($"Real asset has a highlight but the part has none ({staleHighlight.Count}):");
                lines.AddRange(staleHighlight.OrderBy(s => s).Select(s => "  " + s));
                var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
                File.WriteAllLines(Path.Combine(outDir, "placeholder_check.txt"), lines);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[PlaceholderCheck] could not write placeholder_check.txt: " + ex.Message);
            }
        }
    }
}
