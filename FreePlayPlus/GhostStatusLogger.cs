using System.Linq;
using BBI.Unity.Game;

namespace FreePlayPlus
{
    // One line per ship spawn, any session and whether or not the checkbox is on, stating whether
    // the ship actually carries the ghost property -- the ghost override's name alone doesn't say
    // (its ghost affix is a 15% roll), and stock FreePlay launches log nothing ghost-related.
    internal static class GhostStatusLogger
    {
        public static void Register()
        {
            Main.EventSystem.AddHandler<ShipAllSpawnWrapperEvent>(OnShipAllSpawnWrapperEvent);
            Main.EventSystem.AddHandler<UnlockAbilityEvent>(ev => Plugin.Debug($"UnlockAbilityEvent {ev.Ability} (FreePlay={FreePlayDamage.IsFreePlay})."));
        }

        private static void OnShipAllSpawnWrapperEvent(ShipAllSpawnWrapperEvent ev)
        {
            if (ev.State == ShipAllSpawnWrapperEvent.SpawnState.Complete) SavedShipPrimer.ReleaseHeld();
            if (ev.State != ShipAllSpawnWrapperEvent.SpawnState.Complete || ev.ShipPreview == null) return;
            var preview = ev.ShipPreview;

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
