using BBI;
using HarmonyLib;

namespace NewtonianPhysics
{
    // VelocityUIController.Update turns the HUD's M/S readout red once velocity crosses
    // m_DangerVelocity (asset default 20) every frame - a reasonable warning threshold at stock's
    // low speed cap, but with MaxVelocityMps raised well above that, the readout turns red for the
    // vast majority of this mod's usable speed range instead of only near the actual limit. This
    // instead derives the threshold from whichever ceiling truly applies - MaxVelocityMps, or the
    // ~199.65 m/s hard engine limit if that's lower - so red only means "within 1 m/s of as fast
    // as you can actually go," not an arbitrary stock number.
    internal static class VelocityHudTuning
    {
        // The physics engine silently clamps Rigidbody velocity to this magnitude regardless of
        // any scripted MaxVelocity field - confirmed directly via logged Rigidbody.velocity that
        // plateaued at exactly this value under both max thrust and max-force recoil, dead flat
        // with zero per-tick variance (see project notes) - not a value exposed anywhere in
        // managed code, so it's hardcoded here rather than read from any game field.
        private const float EngineVelocityCeiling = 199.65f;

        [HarmonyPatch(typeof(VelocityUIController), "Update")]
        private static class VelocityUIController_Update_DangerThreshold
        {
            private static void Prefix(VelocityUIController __instance)
            {
                float configuredMax = Plugin.ConfigMaxVelocityMps.Value;
                float effectiveMax = configuredMax > 0f
                    ? System.Math.Min(configuredMax, EngineVelocityCeiling)
                    : EngineVelocityCeiling;

                Traverse.Create(__instance).Field("m_DangerVelocity").SetValue(effectiveMax - 1f);
            }
        }
    }
}
