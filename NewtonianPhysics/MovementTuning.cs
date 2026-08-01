using System.Collections.Generic;
using BBI;
using BBI.Unity.Game;
using HarmonyLib;

namespace NewtonianPhysics
{
    // Two vanilla movement limits unrelated to drag: a hard speed cap, and a "work area" leash
    // around the level's designated work zones that eventually teleports or damages you for
    // straying too far.
    internal static class MovementTuning
    {
        // RigidbodyController.MaxVelocity (PlayerMotion's base class) is a plain public field read
        // directly every FixedUpdate to clamp mRigidbody.velocity. Writing it once in Awake looked
        // sufficient but isn't: nothing else ever re-reads the config afterward, so editing
        // MaxVelocityMps in the live-reloaded .cfg had no effect until the next shift/respawn
        // recreated the PlayerMotion instance. Matches MagBoots' approach elsewhere in this file
        // (PlayableArea_GetPlayableAreaState_Multiplier) and in the sibling mod: never cache a
        // config value into a game field once, always re-read Plugin.ConfigX.Value at the point of
        // use. Prefixing FixedUpdate (rather than Awake) means this now writes MaxVelocity fresh
        // every physics tick, immediately before base.FixedUpdate() reads it for the clamp - same
        // per-tick cost class as DragTuning's existing FixedUpdate-scoped patches, negligible next
        // to a single float field write.
        // 0 in config means "no cap" - Unity has no built-in infinite Rigidbody speed limit via this
        // field, so use float.MaxValue instead, which is effectively unbounded for gameplay purposes.
        [HarmonyPatch(typeof(PlayerMotion), "FixedUpdate")]
        private static class PlayerMotion_FixedUpdate_MaxVelocity
        {
            private static void Prefix(PlayerMotion __instance)
            {
                float configured = Plugin.ConfigMaxVelocityMps.Value;
                __instance.MaxVelocity = configured > 0f ? configured : float.MaxValue;
            }
        }

        // PlayableArea.GetPlayableAreaState is the single choke point (used by both the UI warning
        // and the teleport/damage enforcement) that measures distance from the player to the
        // nearest of several level-placed work-zone nodes against m_WarningRadius/m_DangerRadius/
        // m_ObjectDangerRadius. All three are private serialized fields with no public setters, so
        // Traverse is required. Scaling them via a Prefix (rather than hardcoding a replacement)
        // preserves whatever relative spacing each level authors between its warning/danger/object
        // radii. Multiplier of 0 short-circuits to Safe directly instead of scaling to a 0m radius,
        // since a 0m radius would make GetPlayableAreaState's own "dist < radius" checks fail
        // immediately and report Danger everywhere - the opposite of "unlimited".
        [HarmonyPatch(typeof(PlayableArea), "GetPlayableAreaState")]
        private static class PlayableArea_GetPlayableAreaState_Multiplier
        {
            private static readonly Dictionary<PlayableArea, (float Warning, float Danger, float ObjectDanger)> sOriginalRadii = new();

            private static bool Prefix(PlayableArea __instance, ref PlayableArea.PlayableAreaState __result)
            {
                float multiplier = Plugin.ConfigWorkAreaRadiusMultiplier.Value;

                var traverse = Traverse.Create(__instance);
                if (!sOriginalRadii.TryGetValue(__instance, out var original))
                {
                    original = (
                        traverse.Field("m_WarningRadius").GetValue<float>(),
                        traverse.Field("m_DangerRadius").GetValue<float>(),
                        traverse.Field("m_ObjectDangerRadius").GetValue<float>());
                    sOriginalRadii[__instance] = original;
                }

                if (multiplier <= 0f)
                {
                    __result = PlayableArea.PlayableAreaState.Safe;
                    return false;
                }

                traverse.Field("m_WarningRadius").SetValue(original.Warning * multiplier);
                traverse.Field("m_DangerRadius").SetValue(original.Danger * multiplier);
                traverse.Field("m_ObjectDangerRadius").SetValue(original.ObjectDanger * multiplier);
                return true;
            }
        }
    }
}
