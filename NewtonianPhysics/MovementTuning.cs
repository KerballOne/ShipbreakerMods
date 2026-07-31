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
        // directly every FixedUpdate to clamp mRigidbody.velocity - no caching, so setting it once
        // whenever the player controller wakes up is enough; no need to patch FixedUpdate itself.
        // 0 in config means "no cap" - Unity has no built-in infinite Rigidbody speed limit via this
        // field, so use float.MaxValue instead, which is effectively unbounded for gameplay purposes.
        [HarmonyPatch(typeof(PlayerMotion), "Awake")]
        private static class PlayerMotion_Awake_MaxVelocity
        {
            private static void Postfix(PlayerMotion __instance)
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
