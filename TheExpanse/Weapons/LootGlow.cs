using BBI.Unity.Game;

namespace TheExpanse
{
    // Spilled loot on the suit's scanner: while the scanner is on in its Objects mode, the HUD shows the same green
    // hexagon markers (count inside, distance under) as from the pilot's seat (FlightHud.ShowLootMarkers).
    internal static class LootGlow
    {
        static bool sLogged;

        // The scanner is on, in Objects mode.
        internal static bool ObjectScan { get; private set; }

        // From the game's ScannerStateChangedEvent (posted when the scanner goes on or off and when its mode changes).
        internal static void OnScanner(ScannerStateChangedEvent ev)
        {
            ObjectScan = Plugin.StationScannerMarkers.Value && ev.State == Scanner.ScannerState.Activated
                         && ev.ScanMode != null && ev.ScanMode.ModeType == ScanModeAsset.ScannerMode.Object;
            if (ObjectScan && !sLogged && StationLoot.Any)
            {
                sLogged = true;
                Plugin.Log.LogInfo($"[Stations] scanner on objects: {StationLoot.Items.Count} spilled collectible(s) marked.");
            }
        }

        internal static void Reset()
        {
            ObjectScan = false;
            sLogged = false;
        }
    }
}
