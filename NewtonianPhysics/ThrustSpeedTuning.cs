using BBI.Unity.Game;
using HarmonyLib;

namespace NewtonianPhysics
{
    // ThrustController.FixedUpdate zeroes thrust force per-axis once velocity along that axis
    // reaches mData.MaxThrustSpeedFree (or MaxThrustSpeedGrappled while holding something heavy)
    // - a soft, thrust-only speed governor completely separate from RigidbodyController.MaxVelocity
    // (the hard clamp MovementTuning.cs raises). Confirmed via direct testing: raising MaxVelocity
    // alone did nothing for actual achievable speed under thrust, because thrust simply stops
    // producing acceleration once it hits this second, independent ceiling first - so both are
    // driven off the same ConfigMaxVelocityMps value, keeping a single "how fast can I go" knob
    // instead of two separate ones that both need raising to actually reach higher speeds.
    //
    // MaxThrustSpeedFree/Grappled are read-only computed properties (IThrusterData, buffed via
    // ThrustersBuffableData.GetBuffedValue) with no setter, so the fix is a Postfix on the
    // concrete getters rather than assigning a field directly.
    internal static class ThrustSpeedTuning
    {
        [HarmonyPatch(typeof(ThrustersBuffableData), nameof(ThrustersBuffableData.MaxThrustSpeedFree), MethodType.Getter)]
        private static class ThrustersBuffableData_MaxThrustSpeedFree_Override
        {
            private static void Postfix(ref float __result)
            {
                float configured = Plugin.ConfigMaxVelocityMps.Value;
                if (configured > 0f)
                    __result = configured;
            }
        }

        [HarmonyPatch(typeof(ThrustersBuffableData), nameof(ThrustersBuffableData.MaxThrustSpeedGrappled), MethodType.Getter)]
        private static class ThrustersBuffableData_MaxThrustSpeedGrappled_Override
        {
            private static void Postfix(ref float __result)
            {
                float configured = Plugin.ConfigMaxVelocityMps.Value;
                if (configured > 0f)
                    __result = configured;
            }
        }
    }
}
