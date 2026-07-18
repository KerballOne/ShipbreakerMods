using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FixPropJump
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<float> GrappleCreakAngleScale = null!;
        internal static ConfigEntry<bool> LogSuppressions = null!;
        internal static ConfigEntry<bool> LogJointLifecycle = null!;

        private void Awake()
        {
            Log = Logger;

            GrappleCreakAngleScale = Config.Bind(
                "General", "GrappleCreakAngleScale", 0f,
                "Multiplier (0.0-1.0) applied to BreakableJointComponent's joint-stress \"creak\" " +
                "rotation (Transform.RotateAround) when the force source is the grapple. 0 = fully " +
                "disabled (original bug report: parts snapped position/rotation instantly on a hard " +
                "yank). 1 = vanilla behavior.");

            LogSuppressions = Config.Bind(
                "General", "LogSuppressions", true,
                "If true, logs a line every time the grapple-creak rotation is scaled, including the " +
                "part name and original/scaled angle. Useful for correlating with disjoint issues; " +
                "leave on until the fix is confirmed stable, then turn off to reduce log spam.");

            LogJointLifecycle = Config.Bind(
                "General", "LogJointLifecycle", true,
                "If true, logs every BreakableJointComponent.ApplyForce call (part name, force source, " +
                "magnitude, JointStress before/after, durability fraction remaining) and every actual " +
                "BreakJoints call (part name, force source, whether it was treated as a full-group " +
                "detach or single-joint break). Used to correlate the grapple-creak fix with reports " +
                "of parts not disjointing -- lets you see whether a stuck part ever actually reached " +
                "BreakJoints at all, or got stuck accumulating JointStress instead.");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} loaded. GrappleCreakAngleScale={GrappleCreakAngleScale.Value}");
        }
    }

    // BreakableJointComponent.ApplyForce plays a "joint stress creak" effect: as accumulated
    // JointStress crosses each configured ThresholdData.Threshold, it does a hard
    // Transform.RotateAround (position AND rotation, no velocity) on the part -- and, via the
    // same StructureGroupMember's PartConnection buffer, on every jointed sibling part too. This
    // is meant as a subtle pre-break warning wobble, but on a strong grapple yank enough
    // JointStress can accumulate in a single tick to cross a threshold immediately, and the
    // resulting RotateAround reads as an instant position/rotation snap rather than a creak --
    // this is what players see as the part "jumping" the moment they yank it, even before the
    // joint actually breaks.
    //
    // A transpiler patch on ApplyForce itself was tried first and confirmed (by A/B testing with
    // the plugin fully disabled, then rebuilt with the transpiler removed) to be the direct cause
    // of a separate regression: parts would break (BreakJoints fired, IsBroken=true) but the
    // grapple would keep calling ApplyForce on them forever afterward with escalating force,
    // meaning they never disjointed from the structure graph. Root cause wasn't fully diagnosed at
    // the IL level, but rewriting ApplyForce's body via CodeInstruction insertion carries real risk
    // of corrupting its control flow/local state in ways that are hard to verify by inspection
    // alone. This approach avoids that entirely: instead of rewriting ApplyForce, we patch
    // Transform.RotateAround directly (a small, simple, leaf method) and gate the scaling with a
    // static flag that's only set true for the duration of an ApplyForce call sourced from the
    // grapple. ApplyForce's own IL is never touched.
    [HarmonyPatch(typeof(BreakableJointComponent), nameof(BreakableJointComponent.ApplyForce))]
    internal static class BreakableJointComponent_ApplyForce_TrackGrappleCreak
    {
        private static void Prefix(BreakableJointComponent.ForceAppliedBy forceSource, out bool __state)
        {
            __state = GrappleRotateAroundScaler.InGrappleApplyForce;
            GrappleRotateAroundScaler.InGrappleApplyForce = forceSource == BreakableJointComponent.ForceAppliedBy.Grapple;
        }

        private static void Postfix(bool __state)
        {
            GrappleRotateAroundScaler.InGrappleApplyForce = __state;
        }
    }

    // Prefix on Transform.RotateAround itself: scales the angle when called from inside a
    // grapple-sourced BreakableJointComponent.ApplyForce (see the flag toggled above). Harmless
    // no-op for every other RotateAround call in the game (the flag is false).
    [HarmonyPatch(typeof(Transform), nameof(Transform.RotateAround), new[] { typeof(Vector3), typeof(Vector3), typeof(float) })]
    internal static class GrappleRotateAroundScaler
    {
        // Not thread-related; this is a simple re-entrancy/call-scope flag on Unity's single main
        // thread, toggled by the ApplyForce Prefix/Postfix pair around the original call.
        internal static bool InGrappleApplyForce;

        private static void Prefix(Transform __instance, ref float angle)
        {
            if (!InGrappleApplyForce) return;

            var scale = Plugin.GrappleCreakAngleScale.Value;
            var original = angle;
            angle *= scale;

            if (Plugin.LogSuppressions.Value && original != angle)
            {
                Plugin.Log.LogInfo($"[FixPropJump] Grapple creak on '{__instance.name}': {original:F2} deg -> {angle:F2} deg (scale={scale:F2})");
            }
        }
    }

    // Logs JointStress accumulation on every ApplyForce call, so a part that never reaches
    // BreakJoints (stuck accumulating stress, or silently rejected by IsBroken/IsBreakableBy)
    // can be told apart from one that does break but fails to visually/physically separate.
    [HarmonyPatch(typeof(BreakableJointComponent), nameof(BreakableJointComponent.ApplyForce))]
    internal static class BreakableJointComponent_ApplyForce_Logging
    {
        private static void Prefix(BreakableJointComponent __instance, out float __state)
        {
            __state = __instance.JointStress;
        }

        private static void Postfix(BreakableJointComponent __instance, float forceMagnitude, BreakableJointComponent.ForceAppliedBy forceSource, float __state)
        {
            if (!Plugin.LogJointLifecycle.Value) return;
            var partName = __instance.StructurePart != null ? __instance.StructurePart.name : "?";
            var isBroken = __instance.IsBroken;
            Plugin.Log.LogInfo(
                $"[FixPropJump] ApplyForce on '{partName}' source={forceSource} force={forceMagnitude:F2} " +
                $"stress {__state:F1} -> {__instance.JointStress:F1} durabilityLeft={__instance.GetJointDurability:F2} broken={isBroken}");
        }
    }

    // BreakJoints is where the joint is actually marked Broken and RemoveHierarchyJointsEvent is
    // posted to detach the part from the structure graph. Logging here confirms whether a part
    // that appears "stuck" ever reached this method at all.
    [HarmonyPatch(typeof(BreakableJointComponent), "BreakJoints")]
    internal static class BreakableJointComponent_BreakJoints_Logging
    {
        private static void Postfix(BreakableJointComponent __instance, Vector3 breakDir, BreakableJointComponent.ForceAppliedBy forceSource)
        {
            if (!Plugin.LogJointLifecycle.Value) return;
            var partName = __instance.StructurePart != null ? __instance.StructurePart.name : "?";
            Plugin.Log.LogInfo($"[FixPropJump] BreakJoints on '{partName}' source={forceSource} dir={breakDir}");
        }
    }
}
