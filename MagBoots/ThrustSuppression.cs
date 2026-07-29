using BBI.Unity.Game;
using HarmonyLib;

namespace MagBoots
{
    // Stops the player's own thrusters from fighting MagBoots' spring hold while attached - without this,
    // any thrust input (including the same tangential input MagBoots itself reads to walk) still applies
    // vanilla's own thrust force, drains fuel, and rumbles the controller underneath MagBoots' movement,
    // none of which does anything useful once attached.
    internal static class ThrustSuppression
    {
        private static bool sSuppressed;

        // Toggled from MagBoots' own attach/detach lifecycle (BeginSnap/Detach/OnLeaveGameplay) - suppress
        // starts at the same moment the snap tween begins (Locking), not just once fully Locked, so
        // thrust doesn't fight the initial snap either.
        public static void SetSuppressed(bool suppressed) => sSuppressed = suppressed;

        // ThrustController.Update is where vanilla reads thrust input, computes mVelocity, drains fuel,
        // and calls InputManager.ActiveDevice.Vibrate(...) - all in one method. FixedUpdate (below) then
        // just applies whatever mVelocity Update last computed as a physics force. Skipping BOTH means
        // input is still read fine by MagBoots' own class elsewhere (a separate, unrelated code path), but
        // vanilla never computes a force, never drains Charge, and never vibrates while suppressed.
        [HarmonyPatch(typeof(ThrustController), "Update")]
        private static class ThrustController_Update_Suppress
        {
            private static bool Prefix(ThrustController __instance)
            {
                if (!sSuppressed)
                    return true;

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"MagBoots: thrust suppressed - Charge={__instance.Charge}/{__instance.MaxCharge} (should stay unchanged while attached).");

                return false;
            }
        }

        [HarmonyPatch(typeof(ThrustController), "FixedUpdate")]
        private static class ThrustController_FixedUpdate_Suppress
        {
            private static bool Prefix() => !sSuppressed;
        }
    }
}
