using System.Linq;
using BBI.Unity.Game;

namespace FreePlayPlus
{
    // Ship spawn events, any session:
    //   - when a ship finishes spawning, release the prefabs primed for a saved custom ship and log
    //     what the saved-ship asset locator supplied;
    //   - one [GhostStatus] line per spawned ship, whether or not the checkbox is on, stating whether
    //     it actually carries the ghost property -- the ghost override's name alone doesn't say (its
    //     ghost affix is a 15% roll), and stock FreePlay launches log nothing ghost-related.
    internal static class ShipSpawnEvents
    {
        public static void Register() =>
            Main.EventSystem.AddHandler<ShipAllSpawnWrapperEvent>(OnShipAllSpawnWrapperEvent);

        private static void OnShipAllSpawnWrapperEvent(ShipAllSpawnWrapperEvent ev)
        {
            // Despawns post the same event (Type Despawn) when the ship is torn down; only spawns count.
            if (ev.State != ShipAllSpawnWrapperEvent.SpawnState.Complete || ev.Type == ShipAllSpawnWrapperEvent.SpawnType.Despawn) return;
            SavedShipPrimer.ReleaseHeld();
            if (ev.ShipPreview == null) return;
            CustomShipAssetLocator.LogSpawnTotal(ev.ShipPreview.ConstructionAssetName);
            LogGhostStatus(ev.ShipPreview);
        }

        private static void LogGhostStatus(ShipPreview preview)
        {
            var ghostAffix = preview.Properties?.Affixes?.FirstOrDefault(GhostShipCatalog.IsGhost);
            var source = ghostAffix == null ? ""
                : preview == PendingGhost.ActivePreview ? " (forced by checkbox)"
                : " (rolled by the game)";
            var overrides = preview.PropertyOverridesAsset != null ? preview.PropertyOverridesAsset.name : "none";

            Plugin.Log.LogInfo($"[GhostStatus] {preview.ConstructionAssetName} '{preview.ShipName}' " +
                $"session={GameSession.CurrentSessionType} ghost={(ghostAffix != null ? "YES" : "no")}{source} overrides={overrides}");
        }
    }
}
