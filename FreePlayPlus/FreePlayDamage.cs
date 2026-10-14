using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;

namespace FreePlayPlus
{
    // FreePlay's "no damage" is Player.SetGodMode(true), which adds a single Invulnerable component
    // to the player entity. Every vitality system excludes Invulnerable, so it blocks health and suit
    // damage *and* healing, oxygen changes, oxygen drain, and asphyxiation all at once.
    // With HealthDamage or ConsumeOxygen on, Invulnerable is removed again right after the
    // game adds it (Player.Start, and every respawn):
    //   - HealthDamage brings back health/suit damage from impacts and hazards; healing uses
    //     the same ModifyHealth/ModifySuit path, so suit repair pickups work too. When it's off, the
    //     narrower InvulnerableHealth + InvulnerableSuit flags are added instead, which also block
    //     asphyxiation.
    //   - Oxygen has no separate invulnerability flag. With ConsumeOxygen off it's topped back up to
    //     full every frame to keep FreePlay's unlimited oxygen.
    internal static class FreePlayDamage
    {
        private static bool sLoggedActive;

        internal static bool IsFreePlay =>
            SceneLoader.Instance != null &&
            SceneLoader.Instance.CurrentLevelData.SessionType == GameSession.SessionType.FreeMode;

        private static bool Active => IsFreePlay && (Plugin.HealthDamage.Value || Plugin.ConsumeOxygen.Value);

        internal static void RemoveGodMode(string when)
        {
            if (!Active || !TryGetPlayer(out var entityManager, out var player)) return;
            if (!entityManager.HasComponent<Invulnerable>(player)) return;
            Player.SetGodMode(player, entityManager, enabled: false);
            var healthDamage = Plugin.HealthDamage.Value;
            if (!healthDamage)
            {
                if (!entityManager.HasComponent<InvulnerableHealth>(player)) entityManager.AddComponentData(player, default(InvulnerableHealth));
                if (!entityManager.HasComponent<InvulnerableSuit>(player)) entityManager.AddComponentData(player, default(InvulnerableSuit));
            }
            Plugin.Debug($"FreePlay: removed god mode ({when}).");
            if (!sLoggedActive)
            {
                sLoggedActive = true;
                Plugin.Log.LogInfo($"FreePlay health damage {(healthDamage ? "on" : "off")}, oxygen {(Plugin.ConsumeOxygen.Value ? "consumed" : "unlimited")}.");
            }
        }

        // Called from Plugin.Update.
        internal static void Tick()
        {
            if (!Active || Plugin.ConsumeOxygen.Value || !TryGetPlayer(out var entityManager, out var player)) return;
            if (!entityManager.HasComponent<OxygenComponent>(player)) return;
            var oxygen = entityManager.GetComponentData<OxygenComponent>(player);
            if (oxygen.Current >= oxygen.Max) return;
            oxygen.Current = oxygen.Max;
            entityManager.SetComponentData(player, oxygen);
        }

        private static bool TryGetPlayer(out EntityManager entityManager, out Entity player)
        {
            entityManager = default;
            player = Entity.Null;
            var world = World.DefaultGameObjectInjectionWorld;
            var profile = PlayerProfileService.Instance?.Profile;
            if (world == null || profile == null) return false;
            entityManager = world.EntityManager;
            player = profile.PlayerEntity;
            return player != Entity.Null && entityManager.Exists(player);
        }
    }

    [HarmonyPatch(typeof(Player), "Start")]
    internal static class Player_Start_Patch
    {
        private static void Postfix()
        {
            FreePlayDamage.RemoveGodMode("spawn");
            TimedRefill.Clear("spawn");
        }
    }

    [HarmonyPatch(typeof(Player), "OnRespawned")]
    internal static class Player_OnRespawned_Patch
    {
        private static void Postfix()
        {
            FreePlayDamage.RemoveGodMode("respawn");
            TimedRefill.Clear("respawn");
        }
    }
}
